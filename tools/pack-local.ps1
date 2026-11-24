<#
.SYNOPSIS
  Pack every package an app can take from this repository into one folder, at one version.

.DESCRIPTION
  What an app builds against when it takes Kaldra as packages rather than from source: the engine and its hosts,
  backends and UI adapters, the SDK, VMASharp (a submodule), and Vysara's from its checkout (VysaraSource, by default
  ..\Vysara beside this one), which the Vysara adapter depends on at the same version (but see -VysaraFromPackage).
  The folder is a NuGet feed as it stands (add it as a source). Every package gets the same version, Vysara's included,
  so an app names one: KaldraPackageVersion and KaldraVysaraVersion in BinaryVibrance.Kaldra.Sdk's Sdk.targets, and the version of each
  package it references itself.

  Android's package (BinaryVibrance.Kaldra.Hosting.Android) needs the android workload; -SkipAndroid leaves it out.

  -VysaraFromPackage packs a release: Vysara is left out, and the Vysara adapter is built against Vysara's released
  package at the version Directory.Packages.props pins (VysaraSource none), so its dependency is on a Vysara that was
  released rather than one at this version. .github/workflows/publish.yml packs this way.

.EXAMPLE
  pwsh tools/pack-local.ps1                                  # 0.0.0-local into artifacts/packages
  pwsh tools/pack-local.ps1 -Version 0.2.0-split.1 -Output D:\feeds\kaldra
  pwsh tools/pack-local.ps1 -Version 0.2.0 -VysaraFromPackage   # a release, on Vysara's released package
#>
param(
	[string]$Version = "0.0.0-local",
	[string]$Output = "",
	[string]$Configuration = "Release",
	[switch]$SkipAndroid,
	[switch]$VysaraFromPackage
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $repo "artifacts\packages" }
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$extra = @()
if ($VysaraFromPackage) { $extra += "-p:VysaraSource=none" }
else { $vysara = (dotnet msbuild (Join-Path $repo "BinaryVibrance.Kaldra.UI.Vysara/BinaryVibrance.Kaldra.UI.Vysara.csproj") -getProperty:VysaraSource).Trim() }

# In the order they depend on each other, so a failure names the first package that can't be made
$projects = @(
	"external/VMASharp/VMASharp/VMASharp.csproj",
	"BinaryVibrance.Kaldra.Input/BinaryVibrance.Kaldra.Input.csproj",
	"BinaryVibrance.Kaldra.Core/BinaryVibrance.Kaldra.Core.csproj",
	"BinaryVibrance.Kaldra.Graph/BinaryVibrance.Kaldra.Graph.csproj",
	"BinaryVibrance.Kaldra.Assets/BinaryVibrance.Kaldra.Assets.csproj",
	"BinaryVibrance.Kaldra.Concepts/BinaryVibrance.Kaldra.Concepts.csproj",
	"BinaryVibrance.Kaldra.Backend.Vulkan/BinaryVibrance.Kaldra.Backend.Vulkan.csproj",
	"BinaryVibrance.Kaldra.Backend.DirectX12/BinaryVibrance.Kaldra.Backend.DirectX12.csproj",
	"BinaryVibrance.Kaldra.Backend.WebGPU/BinaryVibrance.Kaldra.Backend.WebGPU.csproj",
	"BinaryVibrance.Kaldra.Hosting.Desktop/BinaryVibrance.Kaldra.Hosting.Desktop.csproj",
	"BinaryVibrance.Kaldra.Hosting.Web/BinaryVibrance.Kaldra.Hosting.Web.csproj",
	"BinaryVibrance.Kaldra.UI.Vysara/BinaryVibrance.Kaldra.UI.Vysara.csproj",
	"BinaryVibrance.Kaldra.UI.ImGui/BinaryVibrance.Kaldra.UI.ImGui.csproj",
	"build/BinaryVibrance.Kaldra.Sdk/BinaryVibrance.Kaldra.Sdk.csproj"
)
if (-not $VysaraFromPackage) {
	# Vysara's two packages from its checkout, at the same version as the adapter that depends on them
	$projects = @($projects[0], (Join-Path $vysara "BinaryVibrance.Vysara/BinaryVibrance.Vysara.csproj"),
		(Join-Path $vysara "BinaryVibrance.Vysara.Diagnostics.Endpoint/BinaryVibrance.Vysara.Diagnostics.Endpoint.csproj")) + $projects[1..($projects.Count - 1)]
}
if (-not $SkipAndroid) {
	$projects += "BinaryVibrance.Kaldra.Hosting.Android/BinaryVibrance.Kaldra.Hosting.Android.csproj"
}

foreach ($project in $projects) {
	Write-Host "[pack] $project"
	dotnet pack ([IO.Path]::Combine($repo, $project)) -c $Configuration -o $Output "-p:Version=$Version" -p:UseDebugVisualization=false @extra --nologo -v:q
	if ($LASTEXITCODE) { throw "pack-local: $project did not pack." }
}
$made = @(Get-ChildItem -LiteralPath $Output -Filter "*.$Version.nupkg")
Write-Host "[pack] $($made.Count) packages at $Version in $Output"
