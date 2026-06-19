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
				current = Downsample(current);
				levels.Add(current);
			}
		}

		return new TextureData(format, width, height, levels);
	}

	/// <summary>
	/// Halve a level with a 2×2 integer box filter (rounded average per channel; odd/edge taps clamp to the
	/// last source texel). Pure integer math so the result is bit-for-bit reproducible across machines/backends.
	/// </summary>
	private static MipLevel Downsample(MipLevel src)
	{
		var sw = src.Width;
		var sh = src.Height;
		var dw = Math.Max(1, sw >> 1);
		var dh = Math.Max(1, sh >> 1);
		var dst = new byte[dw * dh * 4];
		var s = src.Pixels;

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
					dst[d + c] = (byte)((s[i00 + c] + s[i01 + c] + s[i10 + c] + s[i11 + c] + 2) >> 2);
			}
		}

		return new MipLevel(dw, dh, dst);
	}
}
