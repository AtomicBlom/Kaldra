using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Materials;

namespace BinaryVibrance.Kaldra.Backend.WebGPU;

/// <summary>
/// Translates the backend-neutral <see cref="Format"/> vocabulary into WebGPU texture formats. Formats cross
/// the interop boundary as small integer ids; <c>kaldra-webgpu.js</c> holds the mirror table mapping each id to
/// the WGSL format string, so the per-frame command stream never carries strings.
/// </summary>
internal static class WebGpuFormatMapping
{
	// Ids must match the FORMATS table in kaldra-webgpu.js exactly.
	public const int Rgba8UnormSrgb = 1;
	public const int Rgba8Unorm = 2;
	public const int Rgb10A2Unorm = 3;
	public const int Rgba16Float = 4;
	public const int R16Float = 5;
	public const int R32Uint = 6;
	public const int Rg16Float = 7;
	public const int Depth32Float = 8;
	public const int R32Float = 9;
	public const int Bgra8Unorm = 10;
	public const int Bgra8UnormSrgb = 11;

	/// <summary>Map an abstract resource format to its WebGPU format id.</summary>
	public static int ToWebGpu(Format format) => format switch
	{
		Format.Rgba8Srgb => Rgba8UnormSrgb,
		Format.Rgba8Unorm => Rgba8Unorm,
		Format.Bgra8Srgb => Bgra8UnormSrgb,
		Format.Rgb10A2 => Rgb10A2Unorm,
		Format.Rgba16F => Rgba16Float,
		Format.R16F => R16Float,
		Format.R32Uint => R32Uint,
		Format.Rg16F => Rg16Float,
		Format.Depth32F => Depth32Float,
		Format.R32F => R32Float,
		_ => throw new WebGpuException($"No WebGPU format mapping for {format}."),
	};

	/// <summary>Map a source-image encoding to its WebGPU format id.</summary>
	public static int ToWebGpu(TextureFormat format) => format switch
	{
		TextureFormat.Rgba8Srgb => Rgba8UnormSrgb,
		TextureFormat.Rgba8Unorm => Rgba8Unorm,
		TextureFormat.Rgba16F => Rgba16Float,
		_ => throw new WebGpuException($"No WebGPU format mapping for texture format {format}."),
	};

	/// <summary>Bytes per texel of a source-image encoding — sizes each mip's upload row.</summary>
	public static int BytesPerTexel(TextureFormat format) => format switch
	{
		TextureFormat.Rgba8Srgb or TextureFormat.Rgba8Unorm => 4,
		TextureFormat.Rgba16F => 8,
		_ => throw new WebGpuException($"No texel size for texture format {format}."),
	};

	/// <summary>The format id of the canvas backbuffer given the browser's preferred format name. The canvas is
	/// configured with the plain format and rendered through an sRGB view, matching the other backends' sRGB
	/// swapchains so shared shaders need no encode branch.</summary>
	public static (int Canvas, int View) CanvasFormats(string preferred) => preferred switch
	{
		"rgba8unorm" => (Rgba8Unorm, Rgba8UnormSrgb),
		_ => (Bgra8Unorm, Bgra8UnormSrgb),
	};
}
