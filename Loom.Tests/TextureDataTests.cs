using Loom.Core;

namespace Loom.Tests;

/// <summary>
/// The neutral texture/mip-chain producer: dimensions, level counts, and the deterministic box filter that
/// keeps both backends byte-identical (PlanningPhase2.md M11).
/// </summary>
public class TextureDataTests
{
	private static byte[] Solid(int w, int h, byte r, byte g, byte b, byte a)
	{
		var px = new byte[w * h * 4];
		for (var i = 0; i < w * h; i++)
		{
			px[i * 4 + 0] = r;
			px[i * 4 + 1] = g;
			px[i * 4 + 2] = b;
			px[i * 4 + 3] = a;
		}
		return px;
	}

	[Test]
	[Arguments(8, 8, 4)]      // 8,4,2,1
	[Arguments(256, 256, 9)]  // 256..1
	[Arguments(4, 2, 3)]      // 4x2, 2x1, 1x1
	[Arguments(1, 1, 1)]      // already minimal
	public void Mip_chain_runs_down_to_1x1(int w, int h, int expectedLevels)
	{
		var data = TextureData.FromRgba8(w, h, Solid(w, h, 10, 20, 30, 40), TextureFormat.Rgba8Unorm);

		data.Levels.Count.ShouldBe(expectedLevels);
		var last = data.Levels[^1];
		last.Width.ShouldBe(1);
		last.Height.ShouldBe(1);
	}

	[Test]
	public void Mip_level_dimensions_halve_and_never_drop_below_1()
	{
		var data = TextureData.FromRgba8(4, 2, Solid(4, 2, 1, 2, 3, 4), TextureFormat.Rgba8Unorm);

		(data.Levels[0].Width, data.Levels[0].Height).ShouldBe((4, 2));
		(data.Levels[1].Width, data.Levels[1].Height).ShouldBe((2, 1));
		(data.Levels[2].Width, data.Levels[2].Height).ShouldBe((1, 1));
	}

	[Test]
	public void Each_level_is_tightly_packed_rgba8()
	{
		var data = TextureData.FromRgba8(8, 8, Solid(8, 8, 0, 0, 0, 255), TextureFormat.Rgba8Srgb);

		foreach (var level in data.Levels)
			level.Pixels.Length.ShouldBe(level.Width * level.Height * 4);
	}

	[Test]
	public void Box_filter_averages_a_2x2_block_with_rounding()
	{
		// One 2x2 image, red channel {10, 20, 30, 41}: (10+20+30+41+2)>>2 = 103>>2 = 25.
		var px = new byte[2 * 2 * 4];
		byte[] reds = [10, 20, 30, 41];
		for (var i = 0; i < 4; i++)
		{
			px[i * 4 + 0] = reds[i];
			px[i * 4 + 3] = 255;
		}

		var data = TextureData.FromRgba8(2, 2, px, TextureFormat.Rgba8Unorm);
		var mip = data.Levels[1];

		(mip.Width, mip.Height).ShouldBe((1, 1));
		mip.Pixels[0].ShouldBe((byte)25);   // red
		mip.Pixels[3].ShouldBe((byte)255);  // alpha all 255 → 255
	}

	[Test]
	public void Solid_color_is_preserved_through_every_mip()
	{
		var data = TextureData.FromRgba8(8, 8, Solid(8, 8, 70, 130, 180, 255), TextureFormat.Rgba8Srgb);

		foreach (var level in data.Levels)
		{
			level.Pixels[0].ShouldBe((byte)70);
			level.Pixels[1].ShouldBe((byte)130);
			level.Pixels[2].ShouldBe((byte)180);
			level.Pixels[3].ShouldBe((byte)255);
		}
	}

	[Test]
	public void No_mips_yields_a_single_level()
	{
		var data = TextureData.FromRgba8(8, 8, Solid(8, 8, 1, 1, 1, 1), TextureFormat.Rgba8Unorm, generateMips: false);

		data.Levels.ShouldHaveSingleItem();
		(data.Levels[0].Width, data.Levels[0].Height).ShouldBe((8, 8));
	}

	[Test]
	public void FromRgba8_rejects_a_wrong_length_buffer()
	{
		Should.Throw<ArgumentException>(() =>
			TextureData.FromRgba8(4, 4, new byte[4 * 4 * 3], TextureFormat.Rgba8Unorm));
	}

	[Test]
	[Arguments(0, 4)]
	[Arguments(4, 0)]
	[Arguments(-1, 4)]
	public void FromRgba8_rejects_nonpositive_dimensions(int w, int h)
	{
		Should.Throw<ArgumentOutOfRangeException>(() =>
			TextureData.FromRgba8(w, h, [], TextureFormat.Rgba8Unorm));
	}
}
