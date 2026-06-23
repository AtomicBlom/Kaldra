using Loom.Core.Materials;
using StbImageSharp;

namespace Loom.Assets;

/// <summary>
/// Decodes an image file (PNG/JPG/…) into Loom's neutral <see cref="TextureData"/> via StbImageSharp (a
/// pure-managed stb_image port — no native dependency). The decode is the only graphics-unaware step; the
/// backend's texture store takes the resulting RGBA8 + mip chain and uploads it. A neutral producer beside
/// <c>Loom.Core</c>, with no graphics-API reference.
/// </summary>
public static class ImageLoader
{
	/// <summary>Decode an image file to RGBA8 and build its mip chain.</summary>
	/// <param name="path">Path to a PNG/JPG/… file.</param>
	/// <param name="format">Whether the texels are sRGB colour (albedo) or linear data (normal map, mask).</param>
	/// <param name="generateMips">Generate the full mip chain (default true).</param>
	public static TextureData Load(string path, TextureFormat format, bool generateMips = true)
	{
		if (!File.Exists(path))
			throw new FileNotFoundException($"Image asset not found: '{path}'.", path);
		using var stream = File.OpenRead(path);
		try
		{
			return Decode(stream, format, generateMips);
		}
		catch (Exception ex) when (ex is not FileNotFoundException)
		{
			// Surface a decode failure with the offending asset path rather than a bare RGBA-byte-count error.
			throw new InvalidDataException($"Failed to decode image asset '{path}': {ex.Message}", ex);
		}
	}

	/// <summary>Decode an encoded image from a stream (e.g. a glTF-embedded image) to neutral <see cref="TextureData"/>.</summary>
	public static TextureData Decode(Stream encoded, TextureFormat format, bool generateMips = true)
	{
		var image = ImageResult.FromStream(encoded, ColorComponents.RedGreenBlueAlpha);
		if (image is null || image.Width <= 0 || image.Height <= 0 || image.Data is null)
			throw new InvalidDataException("Image decode produced no pixels (unsupported or corrupt image data).");
		return TextureData.FromRgba8(image.Width, image.Height, image.Data, format, generateMips);
	}

	/// <summary>
	/// Decode a Radiance HDR (<c>.hdr</c>) / floating-point image file to neutral RGBA16F <see cref="TextureData"/>
	/// — the source for an image-based-lighting environment / equirectangular map, whose values exceed 1 and so
	/// would clip in 8-bit unorm.
	/// </summary>
	public static TextureData LoadHdr(string path)
	{
		if (!File.Exists(path))
			throw new FileNotFoundException($"HDR image asset not found: '{path}'.", path);
		using var stream = File.OpenRead(path);
		try
		{
			return DecodeHdr(stream);
		}
		catch (Exception ex) when (ex is not FileNotFoundException)
		{
			throw new InvalidDataException($"Failed to decode HDR image asset '{path}': {ex.Message}", ex);
		}
	}

	/// <summary>Decode an HDR / floating-point image from a stream to RGBA16F <see cref="TextureData"/> (half-float, no mips).</summary>
	public static TextureData DecodeHdr(Stream encoded)
	{
		var image = ImageResultFloat.FromStream(encoded, ColorComponents.RedGreenBlueAlpha);
		if (image is null || image.Width <= 0 || image.Height <= 0 || image.Data is null)
			throw new InvalidDataException("HDR image decode produced no pixels (unsupported or corrupt image data).");

		// stb returns 32-bit float RGBA; store as 16-bit half (IBL precision is ample, halving the memory).
		var src = image.Data;
		var half = new Half[src.Length];
		for (var i = 0; i < src.Length; i++)
			half[i] = (Half)src[i];
		return TextureData.FromRgba16F(image.Width, image.Height, half);
	}
}
