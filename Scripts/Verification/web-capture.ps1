<#
.SYNOPSIS
  Capture a Loom.Web demo frame in headless Edge and (optionally) diff it against a Vulkan reference — the
  authoritative cross-backend pixel-parity check for the WebGPU backend.

.DESCRIPTION
  Serves a published Loom.Web app from a local HttpListener (static files + a POST /capture endpoint), launches
  headless Edge at `?capture=<Name>&post`, and waits for the browser to POST back the PNG it grabs at frame 60.
  The capture is read off the swapchain texture in C# (compositor-independent), so — unlike a CDP screenshot — it
  is reliable in headless mode. With -Reference, it then runs diff.ps1 against a Vulkan capture of the same demo.

  Publish first:  dotnet publish Loom.Web -c Release -o <dir>
  The served root is <dir>/wwwroot.

.EXAMPLE
  pwsh Scripts/Verification/web-capture.ps1 -PublishDir pub/wwwroot -Name cube -Out web-cube.png
  pwsh Scripts/Verification/web-capture.ps1 -PublishDir pub/wwwroot -Name cube -Out web-cube.png -Reference vk-cube.png
#>
param(
	[Parameter(Mandatory)][string]$PublishDir,
	[Parameter(Mandatory)][string]$Name,
	[Parameter(Mandatory)][string]$Out,
	[string]$Reference,
	[int]$Port = 8099,
	[int]$WaitSec = 40,
	[string]$EdgePath = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $PublishDir).Path

$mime = @{
	".html" = "text/html"; ".js" = "text/javascript"; ".mjs" = "text/javascript"; ".json" = "application/json";
	".wasm" = "application/wasm"; ".css" = "text/css"; ".png" = "image/png"; ".ico" = "image/x-icon";
	".dll" = "application/octet-stream"; ".dat" = "application/octet-stream"; ".blat" = "application/octet-stream";
	".woff" = "font/woff"; ".woff2" = "font/woff2"; ".wgsl" = "text/plain"; ".pdb" = "application/octet-stream";
}

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()

$profileDir = Join-Path ([IO.Path]::GetTempPath()) ("web-capture-" + $Port)
$proc = Start-Process $EdgePath -PassThru -ArgumentList @(
	"--headless=new", "--enable-unsafe-webgpu", "--no-sandbox", "--no-first-run", "--disable-extensions",
	"--use-angle=default", "--user-data-dir=$profileDir", "--window-size=1280,720",
	"http://localhost:$Port/?capture=$([Uri]::EscapeDataString($Name))&post")

$deadline = [DateTime]::UtcNow.AddSeconds($WaitSec)
$captured = $false
try {
	while ([DateTime]::UtcNow -lt $deadline) {
		$task = $listener.GetContextAsync()
		if (-not $task.Wait([int]([Math]::Max(1, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)))) { break }
		$ctx = $task.Result
		$req = $ctx.Request
		$res = $ctx.Response
		try {
			if ($req.HttpMethod -eq "POST" -and $req.Url.AbsolutePath -eq "/capture") {
				$ms = [IO.MemoryStream]::new()
				$req.InputStream.CopyTo($ms)
				[IO.File]::WriteAllBytes($Out, $ms.ToArray())
				$res.StatusCode = 204
				$res.Close()
				$captured = $true
				break
			}
			# Static file GET: map the URL path under the served root (default document index.html).
			$rel = [Uri]::UnescapeDataString($req.Url.AbsolutePath.TrimStart('/'))
			if ([string]::IsNullOrEmpty($rel)) { $rel = "index.html" }
			$path = Join-Path $root $rel
			if (Test-Path -LiteralPath $path -PathType Leaf) {
				$bytes = [IO.File]::ReadAllBytes($path)
				$ext = [IO.Path]::GetExtension($path).ToLowerInvariant()
				$res.ContentType = if ($mime.ContainsKey($ext)) { $mime[$ext] } else { "application/octet-stream" }
				$res.ContentLength64 = $bytes.Length
				$res.OutputStream.Write($bytes, 0, $bytes.Length)
			}
			else { $res.StatusCode = 404 }
			$res.Close()
		}
		catch { try { $res.Abort() } catch {} }
	}
}
finally {
	if ($proc -and -not $proc.HasExited) { $proc.Kill() }
	$listener.Stop()
}

if (-not $captured) { Write-Error "No capture received within $WaitSec s (browser never POSTed /capture)."; exit 1 }
Write-Host ("CAPTURED: $Out (" + (Get-Item $Out).Length + " bytes)")

if ($Reference) {
	& (Join-Path $PSScriptRoot "diff.ps1") -A $Out -B $Reference -Out ($Out + ".diff.png")
}
