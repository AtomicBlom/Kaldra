using Loom.Core;

using StbImageSharp;

namespace Loom.Assets;

/// <summary>
/// Decodes an image file (PNG/JPG/…) into Loom's neutral <see cref="TextureData"/> via StbImageSharp (a
/// pure-managed stb_image port — no native dependency). The decode is the only graphics-unaware step; the
/// backend's texture store takes the resulting RGBA8 + mip chain and uploads it. This is the M11 asset path
/// (PlanningPhase2.md §8): a neutral producer beside <c>Loom.Core</c>, with no graphics-API reference.
/// </summary>
public static class ImageLoader
{
	/// <summary>Decode an image file to RGBA8 and build its mip chain.</summary>
	/// <param name="path">Path to a PNG/JPG/… file.</param>
	/// <param name="format">Whether the texels are sRGB colour (albedo) or linear data (normal map, mask).</param>
	/// <param name="generateMips">Generate the full mip chain (default true).</param>
	public static TextureData Load(string path, TextureFormat format, bool generateMips = true)
	{
		using var stream = File.OpenRead(path);
		return Decode(stream, format, generateMips);
	}

	/// <summary>Decode an encoded image from a stream (e.g. a glTF-embedded image) to neutral <see cref="TextureData"/>.</summary>
	public static TextureData Decode(Stream encoded, TextureFormat format, bool generateMips = true)
	{
		var image = ImageResult.FromStream(encoded, ColorComponents.RedGreenBlueAlpha);
		return TextureData.FromRgba8(image.Width, image.Height, image.Data, format, generateMips);
	}
}
