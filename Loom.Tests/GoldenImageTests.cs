namespace Loom.Tests;

/// <summary>
/// M13 golden-image regression. Opt-in: it compares freshly captured frames (in <c>LOOM_GOLDEN_DIR</c>) against the
/// committed baselines under <c>Goldens/</c>, so it is SKIPPED in the normal no-GPU CI run — the GPU-producing
/// capture step happens locally (see <c>tools/golden.ps1</c>); only the pixel comparison lives here, which needs no
/// GPU. A baseline filename must match its capture filename. Same-backend regression should be near-exact.
/// </summary>
public class GoldenImageTests
{
	private static string GoldensDir => Path.Combine(AppContext.BaseDirectory, "Goldens");

	// A captured frame may differ from its baseline by a hair (driver rounding); a real regression moves many
	// pixels. Flag a pixel if any channel differs by > 4/255, and fail only if > 0.5% of pixels are flagged.
	private const int PerChannelTolerance = 4;
	private const double MaxFailedFraction = 0.005;

	[Test]
	public void Captures_match_committed_baselines_within_tolerance()
	{
		// Opt-in: the GPU-producing capture step runs locally (tools/golden.ps1) and sets LOOM_GOLDEN_DIR. Without
		// it — e.g. the no-GPU CI run — this passes vacuously.
		var captureDir = Environment.GetEnvironmentVariable("LOOM_GOLDEN_DIR");
		if (string.IsNullOrEmpty(captureDir))
			return;
		if (!Directory.Exists(GoldensDir) || Directory.GetFiles(GoldensDir, "*.png").Length == 0)
			return;

		var failures = new List<string>();
		foreach (var baseline in Directory.GetFiles(GoldensDir, "*.png"))
		{
			var name = Path.GetFileName(baseline);
			var capture = Path.Combine(captureDir!, name);
			if (!File.Exists(capture))
			{
				failures.Add($"{name}: no capture in LOOM_GOLDEN_DIR");
				continue;
			}

			var (bw, bh, baselinePixels) = PngReader.Read(baseline);
			var (cw, ch, capturePixels) = PngReader.Read(capture);
			if (bw != cw || bh != ch)
			{
				failures.Add($"{name}: size {cw}x{ch} != baseline {bw}x{bh}");
				continue;
			}

			var diff = ImageComparison.Compare(bw, bh, baselinePixels, capturePixels, PerChannelTolerance);
			if (diff.FailedFraction > MaxFailedFraction)
				failures.Add($"{name}: {diff}");
		}

		failures.ShouldBeEmpty("Golden-image regressions detected:\n  " + string.Join("\n  ", failures));
	}
}
