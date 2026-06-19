# The canonical cross-backend parity loop in one command: capture a demo on Vulkan AND DX12 (headless, frozen
# time) then count + locate + diff. Writes <demo>-vk.png / <demo>-dx12.png / <demo>-diff.png to -OutDir and
# prints colour counts, content coverage, and the diff summary. Builds Loom.Sandbox once up front unless -NoBuild.
# Requires the default UseVulkan + UseDX12 build. This is a dev-box convenience (it spawns the windowed sandbox);
# CI uses the xUnit golden harness (Loom.Tests) instead.
param(
  # The EXACT demo name (the robust cross-host key — both hosts match it case-insensitively), e.g.
  # "Sprites + Atlas". An index also works but the two hosts may number demos differently, so prefer the name.
  # An unknown value silently falls back to demo 0 on both backends (the warning is surfaced below).
  [Parameter(Mandatory=$true)][string]$Demo,
  [int]$Frames = 70,
  [string]$OutDir = "$env:TEMP/loom-verify",
  [int]$Threshold = 16,
  [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot/../.."
$proj = Join-Path $root "Loom.Sandbox"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$safe = ($Demo -replace '[^\w\-]', '_')
$vk = Join-Path $OutDir "$safe-vk.png"
$dx = Join-Path $OutDir "$safe-dx12.png"
$df = Join-Path $OutDir "$safe-diff.png"

if (-not $NoBuild) {
  "Building Loom.Sandbox ..."
  & dotnet build $proj -v q | Out-Null
}

function Capture($backend, $out) {
  $env:LOOM_DEMO = $Demo
  $env:LOOM_CAPTURE = $out
  $env:LOOM_SMOKE_FRAMES = "$Frames"
  if ($backend -eq 'DX12') { $env:LOOM_BACKEND = 'DX12' }
  else { Remove-Item Env:\LOOM_BACKEND -ErrorAction SilentlyContinue }
  & dotnet run --project $proj --no-build 2>&1 |
    Select-String -Pattern 'captured|debug layer|error|exception|not a known' |
    ForEach-Object { "  [$backend] $($_.Line.Trim())" }
  Remove-Item Env:\LOOM_DEMO, Env:\LOOM_CAPTURE, Env:\LOOM_SMOKE_FRAMES, Env:\LOOM_BACKEND -ErrorAction SilentlyContinue
}

"Capturing '$Demo' ($Frames frames) on both backends -> $OutDir"
Capture 'Vulkan' $vk
Capture 'DX12'   $dx
""
& "$PSScriptRoot/colorcount.ps1" -Path $vk
& "$PSScriptRoot/colorcount.ps1" -Path $dx
""
& "$PSScriptRoot/coverage.ps1" -Path $vk
& "$PSScriptRoot/coverage.ps1" -Path $dx
""
& "$PSScriptRoot/diff.ps1" -A $vk -B $dx -Out $df -Threshold $Threshold
