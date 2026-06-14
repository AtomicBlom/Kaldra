using Loom.Core;

using DxgiFormat = Silk.NET.DXGI.Format;

namespace Loom.DX12;

/// <summary>
/// Translates the backend-neutral <see cref="Format"/> (Loom.Core) into a <c>DXGI_FORMAT</c> — the DX12
/// counterpart of the Vulkan backend's <c>FormatMapping</c>, and the only place abstract formats become
/// API-specific on this backend.
/// </summary>
internal static class DX12FormatMapping
{
	public static DxgiFormat ToDxgi(Format format) => format switch
	{
		Format.Rgba8Srgb => DxgiFormat.FormatR8G8B8A8UnormSrgb,
		Format.Rgba8Unorm => DxgiFormat.FormatR8G8B8A8Unorm,
		Format.Rgb10A2 => DxgiFormat.FormatR10G10B10A2Unorm,
		Format.Rgba16F => DxgiFormat.FormatR16G16B16A16Float,
		Format.R16F => DxgiFormat.FormatR16Float,
		Format.R32Uint => DxgiFormat.FormatR32Uint,
		Format.Rg16F => DxgiFormat.FormatR16G16Float,
		Format.Depth32F => DxgiFormat.FormatD32Float,
		_ => throw new ArgumentOutOfRangeException(nameof(format), format, "No DXGI format mapping."),
	};
}
