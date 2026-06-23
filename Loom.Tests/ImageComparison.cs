namespace Loom.Tests;

/// <summary>Compares decoded RGBA images for the golden-image harness: a regression check against a committed
/// baseline, and a cross-backend check (Vulkan vs DirectX12) — both expressed as a tolerance on per-channel difference.</summary>
internal static class ImageComparison
{
	/// <summary>Compare two same-size RGBA buffers. A pixel "fails" if any channel differs by more than
	/// <paramref name="perChannelTolerance"/>. Throws if the dimensions differ (a size change is always a regression).</summary>
	public static ImageDiff Compare(int width, int height, byte[] a, byte[] b, int perChannelTolerance)
	{
		if (a.Length != b.Length)
			throw new ArgumentException($"image byte lengths differ: {a.Length} vs {b.Length}");

		var maxDiff = 0;
		long sumDiff = 0;
		var failed = 0;
		var pixels = width * height;
		for (var i = 0; i < pixels; i++)
		{
			var pixelFailed = false;
			for (var c = 0; c < 4; c++)
			{
				var d = Math.Abs(a[i * 4 + c] - b[i * 4 + c]);
				sumDiff += d;
				if (d > maxDiff)
					maxDiff = d;
				if (d > perChannelTolerance)
					pixelFailed = true;
			}
			if (pixelFailed)
				failed++;
		}
		return new ImageDiff(width, height, maxDiff, (double)sumDiff / (pixels * 4), failed, pixels);
	}
}
