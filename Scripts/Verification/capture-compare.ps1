# The canonical cross-backend parity loop in one command: capture a demo on Vulkan AND DirectX12 (headless, frozen
# time) then count + locate + diff. Writes <demo>-vk.png / <demo>-dx12.png / <demo>-diff.png to -OutDir and
# prints colour counts, content coverage, and the diff summary. Builds Loom.Sandbox once up front unless -NoBuild.
# Requires the default UseVulkan + UseDirectX12 build. This is a dev-box convenience (it spawns the windowed sandbox);
# CI uses the TUnit golden harness (Loom.Tests) instead.
param(
  # The demo to capture: a 0-based index, an exact (case-insensitive) name, or a unique case-insensitive
  # substring, e.g. "Sprites + Atlas" or just "sprites" (an ambiguous/unmatched value makes the sandbox exit
  # without rendering, so the capture would be missing — the run's output is surfaced below).
  [Parameter(Mandatory=$true)][string]$Demo,
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
  # The sandbox is configured entirely by command-line options now (no LOOM_* env vars). --capture grabs one
  # frame with frozen time + a fixed delta and exits; --backend selects Vulkan (the default) or DirectX12.
  $runArgs = @('run', '--project', $proj, '--no-build', '--', '--demo', $Demo, '--capture', $out)
  if ($backend -eq 'DirectX12') { $runArgs += @('--backend', 'dx12') }
  & dotnet @runArgs 2>&1 |
    Select-String -Pattern 'captured|debug layer|error|exception|not a known' |
    ForEach-Object { "  [$backend] $($_.Line.Trim())" }
}

"Capturing '$Demo' on both backends -> $OutDir"
Capture 'Vulkan' $vk
Capture 'DirectX12'   $dx
""
& "$PSScriptRoot/colorcount.ps1" -Path $vk
& "$PSScriptRoot/colorcount.ps1" -Path $dx
""
& "$PSScriptRoot/coverage.ps1" -Path $vk
& "$PSScriptRoot/coverage.ps1" -Path $dx
""
& "$PSScriptRoot/diff.ps1" -A $vk -B $dx -Out $df -Threshold $Threshold
