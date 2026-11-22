<#
.SYNOPSIS
  Restore or save compiled shader outputs from a cache directory on this machine.

.DESCRIPTION
  Compiling the Slang shaders is the slowest part of a build, and CI recompiles all of them every run because
  actions/checkout wipes the workspace. This caches the compiled outputs (obj/shaders) on local disk, keyed by a
  hash of everything that can change them, so a build whose shaders are unchanged skips the compiler entirely.

  Deliberately NOT actions/cache: the CI runner is self-hosted, so a local directory costs no network round-trip
  and none of the repository's 10 GB cache allowance. A whole tree is about 4 MB, so hundreds of entries fit in
  under a gigabyte.

  Restore DELETES each destination before copying, rather than merging into it. That is the point: the shader
  targets enumerate the output directory to find the DXIL files they package
  (Directory.GetFiles in Loom.Shaders.targets), so a stale .dxil left behind by a renamed or deleted shader would
  be picked up and shipped. A restored tree is exactly what its key's sources produce — nothing extra can survive.

  The key covers the shader sources, the package versions (which pin the Slang compiler and DXC), and the shader
  targets themselves (which hold the compile flags). Any of those changing must invalidate the cache.

.EXAMPLE
  pwsh tools/shader-cache.ps1 -Mode restore   # copy a matching cached tree in, if there is one
  pwsh tools/shader-cache.ps1 -Mode save      # store this build's outputs under the current key
  pwsh tools/shader-cache.ps1 -Mode restore -Root D:\loom-cache -PruneDays 30
#>
param(
	[Parameter(Mandatory)][ValidateSet("restore", "save")][string]$Mode,
	[string]$Root = $(if ($env:LOOM_SHADER_CACHE) { $env:LOOM_SHADER_CACHE } else { Join-Path $env:LOCALAPPDATA "Loom\ShaderCache" }),
	[int]$PruneDays = 14
)
$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $repo "Loom.slnx"))) {
	throw "shader-cache: '$repo' does not look like the Loom repository root (no Loom.slnx)."
}

# Everything that can change a compiled shader. Sources by content, so a touched-but-identical file is still a hit;
# the props/targets because they carry the compile flags, and Directory.Packages.props because it pins Slang.Sdk and
# the DXC used to sign DXIL.
function Get-CacheKey {
	$parts = [System.Collections.Generic.List[string]]::new()

	$sources = Get-ChildItem -Path $repo -Recurse -Filter *.slang -File |
		Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
		Sort-Object { $_.FullName.Substring($repo.Length).ToLowerInvariant() }
	foreach ($file in $sources) {
		$rel = $file.FullName.Substring($repo.Length).TrimStart('\').ToLowerInvariant()
		$parts.Add("$rel=$((Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash)")
	}

	foreach ($rel in @("Directory.Packages.props", "build\Loom.Sdk\Loom.Shaders.props", "build\Loom.Sdk\Loom.Shaders.targets")) {
		$path = Join-Path $repo $rel
		if (Test-Path -LiteralPath $path) {
			$parts.Add("$($rel.ToLowerInvariant())=$((Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash)")
		}
	}

	$bytes = [System.Text.Encoding]::UTF8.GetBytes(($parts -join "`n"))
	$sha = [System.Security.Cryptography.SHA256]::Create()
	try { return [System.BitConverter]::ToString($sha.ComputeHash($bytes)).Replace("-", "").Substring(0, 32) }
	finally { $sha.Dispose() }
}

# Every compiled-shader output directory in the tree, as repo-relative paths — the unit this caches. Named
# "obj\shaders" by Loom.Shaders.props; a project may add a runtime-identifier subdirectory below it, which the
# whole-directory copy carries along.
function Get-OutputDirs {
	Get-ChildItem -Path $repo -Recurse -Directory -Filter shaders -ErrorAction SilentlyContinue |
		Where-Object { $_.Parent.Name -eq "obj" } |
		ForEach-Object { $_.FullName.Substring($repo.Length).TrimStart('\') }
}

# A destination is only ever removed if it is inside the repo and is an obj\shaders directory. The cache writes
# nowhere else, and a bad -Root cannot turn this into a delete of something that matters.
function Assert-SafeOutputDir([string]$full) {
	$normalized = [System.IO.Path]::GetFullPath($full)
	if (-not $normalized.StartsWith([System.IO.Path]::GetFullPath($repo), [System.StringComparison]::OrdinalIgnoreCase)) {
		throw "shader-cache: refusing to touch '$normalized' — outside the repository."
	}
	if ($normalized.TrimEnd('\') -notmatch '\\obj\\shaders$') {
		throw "shader-cache: refusing to touch '$normalized' — not an obj\shaders directory."
	}
}

function Set-StepOutput([string]$name, [string]$value) {
	Write-Host "[shader-cache] $name=$value"
	if ($env:GITHUB_OUTPUT) { "$name=$value" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
}

# Drop entries nothing has asked for in a while. Restore stamps an entry on every hit, so this is least-recently-used
# rather than oldest-created — a key that keeps being built stays regardless of age.
function Remove-StaleEntries {
	if (-not (Test-Path -LiteralPath $Root)) { return }
	$cutoff = (Get-Date).AddDays(-$PruneDays)
	foreach ($entry in Get-ChildItem -LiteralPath $Root -Directory -ErrorAction SilentlyContinue) {
		if ($entry.LastWriteTime -lt $cutoff) {
			Remove-Item -LiteralPath $entry.FullName -Recurse -Force -ErrorAction SilentlyContinue
			Write-Host "[shader-cache] pruned $($entry.Name) (last used $($entry.LastWriteTime.ToString('yyyy-MM-dd')))"
		}
	}
}

$key = Get-CacheKey
$entry = Join-Path $Root $key

if ($Mode -eq "restore") {
	Remove-StaleEntries
	# An entry holding anything but escaped obj\shaders paths is damaged, as one was when a concurrent save moved its
	# staging directory inside it: it is dropped and the build compiles, rather than failing the run on every restore
	if (Test-Path -LiteralPath $entry) {
		$bad = @(Get-ChildItem -LiteralPath $entry -Directory | Where-Object { $_.Name.Replace("__", "\") -notmatch '\\obj\\shaders$' })
		if ($bad.Count -gt 0) {
			Write-Host "[shader-cache] $key holds $($bad[0].Name), not an output directory; dropping the entry."
			Remove-Item -LiteralPath $entry -Recurse -Force
		}
	}
	if (-not (Test-Path -LiteralPath $entry)) {
		Set-StepOutput "hit" "false"
		Write-Host "[shader-cache] miss $key — the build will compile shaders."
		exit 0
	}

	$restored = 0
	foreach ($stored in Get-ChildItem -LiteralPath $entry -Directory) {
		# The entry's subdirectory names are the repo-relative output paths with separators escaped.
		$rel = $stored.Name.Replace("__", "\")
		$dest = Join-Path $repo $rel
		Assert-SafeOutputDir $dest
		if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
		New-Item -ItemType Directory -Path $dest -Force | Out-Null
		Copy-Item -Path (Join-Path $stored.FullName "*") -Destination $dest -Recurse -Force -ErrorAction SilentlyContinue

		# Stamp every restored file as written now. Copy-Item preserves the original timestamps, which would leave the
		# outputs older than sources a checkout just rewrote — MSBuild would then rebuild all of them and the cache
		# would restore perfectly while saving nothing. The key already proves the content matches; the timestamp
		# only has to tell MSBuild so.
		$stamp = Get-Date
		foreach ($file in Get-ChildItem -LiteralPath $dest -Recurse -File) { $file.LastWriteTime = $stamp }
		$restored++
	}
	(Get-Item -LiteralPath $entry).LastWriteTime = Get-Date # stamp for the LRU prune above
	Set-StepOutput "hit" "true"
	Write-Host "[shader-cache] hit $key — restored $restored output director$(if ($restored -eq 1) { 'y' } else { 'ies' })."
	exit 0
}

# save
$dirs = @(Get-OutputDirs)
if ($dirs.Count -eq 0) {
	Write-Host "[shader-cache] nothing to save — no obj\shaders directories exist."
	exit 0
}
if (Test-Path -LiteralPath $entry) {
	Write-Host "[shader-cache] $key already stored; leaving it alone."
	exit 0
}

# Build into a temporary directory and move it into place, so an interrupted save cannot leave a half-written entry
# that the next restore would treat as complete.
$staging = "$entry.partial-$([System.Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $staging -Force | Out-Null
try {
	foreach ($rel in $dirs) {
		$source = Join-Path $repo $rel
		$target = Join-Path $staging $rel.Replace("\", "__")
		New-Item -ItemType Directory -Path $target -Force | Out-Null
		Copy-Item -Path (Join-Path $source "*") -Destination $target -Recurse -Force -ErrorAction SilentlyContinue
	}
	New-Item -ItemType Directory -Path $Root -Force | Out-Null
	try {
		# Directory.Move, not Move-Item: onto a directory that exists, Move-Item moves the staging directory inside it,
		# which leaves the entry holding a stray .partial directory that every restore refuses. Directory.Move throws.
		[System.IO.Directory]::Move($staging, $entry)
	}
	catch {
		# Another job on this runner stored the same key while this one was staging. Identical content by
		# definition — the key is the content — so the loser just drops its copy rather than failing the build.
		if (-not (Test-Path -LiteralPath $entry)) { throw }
		Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
		Write-Host "[shader-cache] $key was stored concurrently; discarded this copy."
		exit 0
	}
}
catch {
	Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
	throw
}

$size = [math]::Round((Get-ChildItem -LiteralPath $entry -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host "[shader-cache] saved $key — $($dirs.Count) output director$(if ($dirs.Count -eq 1) { 'y' } else { 'ies' }), $size MB."
