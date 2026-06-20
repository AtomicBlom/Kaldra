namespace Loom.Core;

/// <summary>
/// A neutral, GPU-API-free description of a texture and its mip chain — the engine-boundary type
/// <see cref="IEngine.CreateTexture"/> consumes. Produced by an asset loader (<c>Loom.Assets</c>) or built
/// directly from raw pixels via <see cref="FromRgba8"/>; the backend's texture store turns it into a sampled
/// image. Mips are generated here on the CPU (a deterministic integer box filter) so both backends upload
/// byte-identical level data — the cleanest path to cross-backend pixel parity and the reason no GPU
/// blit/mip-gen plumbing is needed (Planning.md §8 backend isolation; PlanningPhase2.md M11).
/// </summary>
public sealed class TextureData
{
	/// <summary>The pixel encoding (drives the sampled image format the backend picks).</summary>
	public TextureFormat Format { get; }

	/// <summary>Level-0 width in texels.</summary>
	public int Width { get; }

	/// <summary>Level-0 height in texels.</summary>
	public int Height { get; }

	/// <summary>The mip chain, finest first: <c>Levels[0]</c> is the base image, the last entry is 1×1
	/// (or the single base level when mips weren't generated).</summary>
	public IReadOnlyList<MipLevel> Levels { get; }

	private TextureData(TextureFormat format, int width, int height, IReadOnlyList<MipLevel> levels)
	{
		Format = format;
		Width = width;
		Height = height;
		Levels = levels;
	}

	/// <summary>
	/// Build a <see cref="TextureData"/> from tightly packed RGBA8 pixels, optionally generating the full mip
	/// chain down to 1×1 with a deterministic integer box filter (so every backend uploads identical mips).
	/// </summary>
	/// <param name="width">Image width in texels (positive).</param>
	/// <param name="height">Image height in texels (positive).</param>
	/// <param name="rgba">Row-major RGBA8 texels; length must equal <c>width*height*4</c>.</param>
	/// <param name="format">The pixel encoding.</param>
	/// <param name="generateMips">When true (default), produce levels down to 1×1; otherwise a single level.</param>
	public static TextureData FromRgba8(int width, int height, ReadOnlySpan<byte> rgba, TextureFormat format, bool generateMips = true)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
		if (rgba.Length != width * height * 4)
			throw new ArgumentException($"Expected {width * height * 4} RGBA8 bytes for a {width}×{height} image, got {rgba.Length}.", nameof(rgba));

		var levels = new List<MipLevel> { new(width, height, rgba.ToArray()) };

		if (generateMips)
		{
			var current = levels[0];
			while (current.Width > 1 || current.Height > 1)
			{
				current = Downsample(current, format);
				levels.Add(current);
			}
		}

		return new TextureData(format, width, height, levels);
	}

	/// <summary>
	/// Halve a level with a 2×2 box filter. An sRGB-encoded colour texture (<see cref="TextureFormat.Rgba8Srgb"/>)
	/// must be averaged in <em>linear</em> light — decode each colour channel to linear, average, re-encode — or
	/// the mips darken (the classic gamma-incorrect-mipmap artefact); alpha is always linear, as are all channels
	/// of an <see cref="TextureFormat.Rgba8Unorm"/> texture. The averaging is integer arithmetic over precomputed
	/// transfer tables, so both backends still upload byte-identical level data. Odd/edge taps clamp to the last
	/// source texel.
	/// </summary>
	private static MipLevel Downsample(MipLevel src, TextureFormat format)
	{
		var sw = src.Width;
		var sh = src.Height;
		var dw = Math.Max(1, sw >> 1);
		var dh = Math.Max(1, sh >> 1);
		var dst = new byte[dw * dh * 4];
		var s = src.Pixels;
		var srgb = format == TextureFormat.Rgba8Srgb;

		for (var dy = 0; dy < dh; dy++)
		{
			var sy0 = dy * 2;
			var sy1 = Math.Min(sy0 + 1, sh - 1);
			for (var dx = 0; dx < dw; dx++)
			{
				var sx0 = dx * 2;
				var sx1 = Math.Min(sx0 + 1, sw - 1);

				var i00 = (sy0 * sw + sx0) * 4;
				var i01 = (sy0 * sw + sx1) * 4;
				var i10 = (sy1 * sw + sx0) * 4;
				var i11 = (sy1 * sw + sx1) * 4;
				var d = (dy * dw + dx) * 4;

				for (var c = 0; c < 4; c++)
				{
					// The three colour channels of an sRGB texture average in linear light; alpha (c == 3) is
					// always linear, as is every channel of a UNORM texture.
					if (srgb && c < 3)
					{
						var lin = SrgbToLinear[s[i00 + c]] + SrgbToLinear[s[i01 + c]]
							+ SrgbToLinear[s[i10 + c]] + SrgbToLinear[s[i11 + c]];
						dst[d + c] = LinearToSrgb[(lin + 2) >> 2];
					}
					else
					{
						dst[d + c] = (byte)((s[i00 + c] + s[i01 + c] + s[i10 + c] + s[i11 + c] + 2) >> 2);
					}
				}
			}
		}

		return new MipLevel(dw, dh, dst);
	}

	// sRGB transfer-function tables for gamma-correct mip averaging, built once. SrgbToLinear maps an 8-bit sRGB
	// code to a 16-bit linear value; LinearToSrgb is its exact nearest-inverse (encode a linear value to the
	// closest sRGB code), so a value that is decoded and re-encoded unchanged round-trips exactly — a solid
	// colour is preserved through every mip. Integer-valued so the per-mip averaging stays exact.
	private static readonly ushort[] SrgbToLinear = BuildSrgbToLinear();
	private static readonly byte[] LinearToSrgb = BuildLinearToSrgb(SrgbToLinear);

	private static ushort[] BuildSrgbToLinear()
	{
		var table = new ushort[256];
		for (var i = 0; i < 256; i++)
		{
			var s = i / 255.0;
			var linear = s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
			table[i] = (ushort)Math.Clamp((int)Math.Round(linear * 65535.0), 0, 65535);
		}
		return table;
	}

	private static byte[] BuildLinearToSrgb(ushort[] srgbToLinear)
	{
		// Nearest-inverse of the forward table. srgbToLinear is strictly increasing, so a single advancing
		// pointer finds, for each 16-bit linear value, the sRGB code whose linear value is closest.
		var table = new byte[65536];
		var code = 0;
		for (var linear = 0; linear < 65536; linear++)
		{
			while (code < 255 && Math.Abs(srgbToLinear[code + 1] - linear) < Math.Abs(srgbToLinear[code] - linear))
				code++;
			table[linear] = (byte)code;
		}
		return table;
	}
}
