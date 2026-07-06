using System.Buffers.Binary;
using System.IO.Compression;

namespace Loom.Graph;

/// <summary>
/// Minimal, dependency-free PNG encoder (8-bit RGBA) for debug framebuffer captures. Uses the built-in
/// <see cref="ZLibStream"/> for the IDAT zlib stream and a hand-rolled CRC-32 — no image library needed. Not
/// optimized (filter type 0 on every scanline); fine for validation grabs. Pure CPU work over a byte span, so it
/// is shared by every backend rather than copied into each.
/// </summary>
public static class PngWriter
{
	private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
	private static readonly uint[] CrcTable = BuildCrcTable();

	public static void WriteRgba(string path, int width, int height, ReadOnlySpan<byte> rgba)
	{
		using var stream = File.Create(path);
		WriteRgba(stream, width, height, rgba);
	}

	/// <summary>Encode an 8-bit RGBA image to <paramref name="stream"/> — the filesystem-free path (a browser
	/// capture encodes into a <see cref="MemoryStream"/> before handing the bytes to JS).</summary>
	public static void WriteRgba(Stream stream, int width, int height, ReadOnlySpan<byte> rgba)
	{
		stream.Write(Signature);

		Span<byte> ihdr = stackalloc byte[13];
		BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
		BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], height);
		ihdr[8] = 8;  // bit depth
		ihdr[9] = 6;  // colour type: RGBA
		ihdr[10] = 0; // deflate
		ihdr[11] = 0; // filter method 0
		ihdr[12] = 0; // no interlace
		WriteChunk(stream, "IHDR"u8, ihdr);

		// Raw image data: each scanline prefixed with a filter byte (0 = none).
		var stride = width * 4;
		var raw = new byte[height * (1 + stride)];
		for (var y = 0; y < height; y++)
		{
			var dst = y * (1 + stride);
			raw[dst] = 0;
			rgba.Slice(y * stride, stride).CopyTo(raw.AsSpan(dst + 1));
		}

		using var compressed = new MemoryStream();
		using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
			zlib.Write(raw, 0, raw.Length);
		WriteChunk(stream, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));

		WriteChunk(stream, "IEND"u8, []);
	}

	private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
	{
		Span<byte> length = stackalloc byte[4];
		BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
		stream.Write(length);
		stream.Write(type);
		stream.Write(data);

		Span<byte> crc = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(type, data));
		stream.Write(crc);
	}

	private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
	{
		var c = 0xFFFFFFFFu;
		foreach (var x in a)
			c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
		foreach (var x in b)
			c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
		return c ^ 0xFFFFFFFFu;
	}

	private static uint[] BuildCrcTable()
	{
		var table = new uint[256];
		for (uint n = 0; n < 256; n++)
		{
			var c = n;
			for (var k = 0; k < 8; k++)
				c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
			table[n] = c;
		}
		return table;
	}
}
