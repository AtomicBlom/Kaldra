using Loom.Core.Materials;

namespace Loom.Tests;

/// <summary>
/// The neutral texture/mip-chain producer: dimensions, level counts, and the deterministic box filter that
/// keeps both backends byte-identical.
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

	[Test]
	public void Srgb_mips_average_in_linear_light_not_gamma_space()
	{
		// A 2x2 red checker of black (0) and white (255). Averaged in LINEAR light the result is ~188 sRGB
		// (linear 0.5 re-encoded), far brighter than the gamma-incorrect 128 a naive byte average gives.
		var px = new byte[2 * 2 * 4];
		byte[] reds = [0, 255, 255, 0];
		for (var i = 0; i < 4; i++)
		{
			px[i * 4 + 0] = reds[i];
			px[i * 4 + 3] = 255;
		}

		var srgb = TextureData.FromRgba8(2, 2, px, TextureFormat.Rgba8Srgb).Levels[1].Pixels[0];
		var unorm = TextureData.FromRgba8(2, 2, px, TextureFormat.Rgba8Unorm).Levels[1].Pixels[0];

		unorm.ShouldBe((byte)128);            // naive gamma-space average
		srgb.ShouldBeGreaterThan((byte)160);  // gamma-correct: linear-light average is noticeably brighter
	}

	[Test]
	public void FromRgba16F_is_a_single_half_float_level()
	{
		Half[] texels = new Half[3 * 2 * 4];
		for (var i = 0; i < texels.Length; i++)
			texels[i] = (Half)(i * 0.25f);

		var data = TextureData.FromRgba16F(3, 2, texels);

		data.Format.ShouldBe(TextureFormat.Rgba16F);
		data.Levels.ShouldHaveSingleItem();
		(data.Levels[0].Width, data.Levels[0].Height).ShouldBe((3, 2));
		data.Levels[0].Pixels.Length.ShouldBe(3 * 2 * 4 * sizeof(ushort)); // 8 bytes per RGBA16F texel
	}

	[Test]
	public void FromRgba16F_rejects_a_wrong_length_buffer()
	{
		Should.Throw<ArgumentException>(() =>
			TextureData.FromRgba16F(4, 4, new Half[4 * 4 * 3]));
	}

	[Test]
	public void FromCubeFaces_is_six_mipped_faces_laid_out_face_major()
	{
		// Six distinct solid faces, 4×4 → mips 4,2,1 = 3 per face, 18 levels total, face-major.
		var faces = new byte[6 * 4 * 4 * 4];
		for (var face = 0; face < 6; face++)
			for (var i = 0; i < 4 * 4; i++)
				faces[(face * 4 * 4 + i) * 4 + 0] = (byte)(face * 40); // a per-face red value

		var data = TextureData.FromCubeFaces(4, faces, TextureFormat.Rgba8Unorm);

		data.IsCube.ShouldBeTrue();
		data.LayerCount.ShouldBe(6);
		data.MipsPerFace.ShouldBe(3);
		data.Levels.Count.ShouldBe(18);
		// Face-major: Levels[face * MipsPerFace] is that face's base level, carrying its red value.
		for (var face = 0; face < 6; face++)
			data.Levels[face * data.MipsPerFace].Pixels[0].ShouldBe((byte)(face * 40));
	}

	[Test]
	public void FromCubeFaces_without_mips_is_one_level_per_face()
	{
		var data = TextureData.FromCubeFaces(8, new byte[6 * 8 * 8 * 4], TextureFormat.Rgba8Unorm, generateMips: false);

		data.IsCube.ShouldBeTrue();
		data.Levels.Count.ShouldBe(6);
		data.MipsPerFace.ShouldBe(1);
	}

	[Test]
	public void FromCubeFaces_rejects_a_wrong_length_buffer()
	{
		Should.Throw<ArgumentException>(() =>
			TextureData.FromCubeFaces(4, new byte[5 * 4 * 4 * 4], TextureFormat.Rgba8Unorm));
	}

	[Test]
	public void Box_filter_clamps_edge_taps_on_a_1xN_column()
	{
		// 1x2 column, reds {40, 80}. The x tap clamps to column 0, so the 1x1 result averages the two rows:
		// (40 + 40 + 80 + 80 + 2) >> 2 = 60. Pins the odd-dimension edge-clamp tap selection.
		var px = new byte[1 * 2 * 4];
		px[0 * 4 + 0] = 40; px[0 * 4 + 3] = 255;
		px[1 * 4 + 0] = 80; px[1 * 4 + 3] = 255;

		var mip = TextureData.FromRgba8(1, 2, px, TextureFormat.Rgba8Unorm).Levels[^1];

		(mip.Width, mip.Height).ShouldBe((1, 1));
		mip.Pixels[0].ShouldBe((byte)60);
	}
}
