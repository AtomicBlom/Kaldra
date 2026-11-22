<#
.SYNOPSIS
  Pack every package an app can take from this repository into one folder, at one version.

.DESCRIPTION
  What an app builds against when it takes Loom as packages rather than from source: the engine and its hosts,
  backends and UI adapters, the SDK, SUIsei and VMASharp (submodules), the audio and its outputs, and the workbench.
  The folder is a NuGet feed as it stands (add it as a source). Every package gets the same version, SUIsei's included,
  so an app names one: LoomPackageVersion and LoomSUIseiVersion in Loom.Sdk's Sdk.targets, and the version of each
  package it references itself.

  Android's packages (Loom.Hosting.Android, Loom.Audio.Android) need the android workload; -SkipAndroid leaves them out.

.EXAMPLE
  pwsh tools/pack-local.ps1                                  # 0.0.0-local into artifacts/packages
  pwsh tools/pack-local.ps1 -Version 0.2.0-split.1 -Output D:\feeds\loom
#>
param(
	[string]$Version = "0.0.0-local",
	[string]$Output = "",
	[string]$Configuration = "Release",
	[switch]$SkipAndroid
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $repo "artifacts\packages" }
New-Item -ItemType Directory -Force -Path $Output | Out-Null

# In the order they depend on each other, so a failure names the first package that can't be made
$projects = @(
	"external/VMASharp/VMASharp/VMASharp.csproj",
	"external/SUIsei/SUIsei/SUIsei.csproj",
	"external/SUIsei/SUIsei.Diagnostics.Endpoint/SUIsei.Diagnostics.Endpoint.csproj",
	"Loom.Input/Loom.Input.csproj",
	"Loom.Core/Loom.Core.csproj",
	"Loom.Graph/Loom.Graph.csproj",
	"Loom.Assets/Loom.Assets.csproj",
	"Loom.Concepts/Loom.Concepts.csproj",
	"Loom.Backend.Vulkan/Loom.Backend.Vulkan.csproj",
	"Loom.Backend.DirectX12/Loom.Backend.DirectX12.csproj",
	"Loom.Backend.WebGPU/Loom.Backend.WebGPU.csproj",
	"Loom.Hosting.Desktop/Loom.Hosting.Desktop.csproj",
	"Loom.Hosting.Web/Loom.Hosting.Web.csproj",
	"Loom.UI.SUIsei/Loom.UI.SUIsei.csproj",
	"Loom.UI.ImGui/Loom.UI.ImGui.csproj",
	"build/Loom.Sdk/Loom.Sdk.csproj",
	"Loom.Audio/Loom.Audio.csproj",
	"Loom.Audio.Synth/Loom.Audio.Synth.csproj",
	"Loom.Audio.Sdl/Loom.Audio.Sdl.csproj",
	"Loom.Audio.Web/Loom.Audio.Web.csproj",
	"Loom.Audio.Workbench/Loom.Audio.Workbench.csproj",
	"Loom.Audio.Workbench.Projects/Loom.Audio.Workbench.Projects.csproj"
)
if (-not $SkipAndroid) {
	$projects += "Loom.Hosting.Android/Loom.Hosting.Android.csproj", "Loom.Audio.Android/Loom.Audio.Android.csproj"
}

foreach ($project in $projects) {
	Write-Host "[pack] $project"
	dotnet pack (Join-Path $repo $project) -c $Configuration -o $Output "-p:Version=$Version" -p:UseDebugVisualization=false --nologo -v:q
	if ($LASTEXITCODE) { throw "pack-local: $project did not pack." }
}
$made = @(Get-ChildItem -LiteralPath $Output -Filter "*.$Version.nupkg")
Write-Host "[pack] $($made.Count) packages at $Version in $Output"
