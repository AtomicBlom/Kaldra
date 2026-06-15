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

	/// <summary>
	/// The trio of DXGI formats a transient texture needs: the format the <em>resource</em> is created with, the
	/// format its render-/depth-stencil <em>view</em> uses, and the format a <em>shader-resource view</em> uses
	/// when the transient is sampled. They differ only for depth: a depth target that is later sampled (e.g. a
	/// shadow map) must be created <c>TYPELESS</c> (R32_TYPELESS), because a typed D32_FLOAT resource cannot carry
	/// an SRV — the typeless resource then carries a typed DSV (D32_FLOAT) and a typed SRV (R32_FLOAT). Colour
	/// formats are identical across all three.
	/// </summary>
	public static (DxgiFormat Resource, DxgiFormat View, DxgiFormat Srv) ToDxgiViews(Format format) => format switch
	{
		Format.Depth32F => (DxgiFormat.FormatR32Typeless, DxgiFormat.FormatD32Float, DxgiFormat.FormatR32Float),
		_ => (ToDxgi(format), ToDxgi(format), ToDxgi(format)),
	};
}
