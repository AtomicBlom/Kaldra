namespace Loom.Tests;

/// <summary>
/// The cross-backend pixel-parity check — the engine's headline invariant: a frame captured on Vulkan and on
/// DX12 must match within the same tolerance the golden harness uses. Opt-in via <c>LOOM_VULKAN_DIR</c> +
/// <c>LOOM_DX12_DIR</c> (set by <c>tools/golden.ps1</c>, which captures both backends); SKIPPED in the no-GPU
/// CI run. Each backend writes identically-named PNGs, so they are matched by filename.
/// <para>
/// This complements <see cref="GoldenImageTests"/>: the golden test pins each backend against a committed
/// baseline; this one asserts the two backends agree with <em>each other</em> directly.
/// </para>
/// </summary>
public class CrossBackendParityTests
{
	// Same bar as the golden harness and Scripts/Verification/diff.ps1: a pixel fails if any channel differs by
	// > 4/255, and the frame fails if > 0.5% of pixels are flagged.
	private const int PerChannelTolerance = 4;
	private const double MaxFailedFraction = 0.005;

	[Test]
	public void Vulkan_and_DX12_captures_match_within_tolerance()
	{
		var vulkanDir = Environment.GetEnvironmentVariable("LOOM_VULKAN_DIR");
		var dx12Dir = Environment.GetEnvironmentVariable("LOOM_DX12_DIR");
		if (string.IsNullOrEmpty(vulkanDir) || string.IsNullOrEmpty(dx12Dir))
			throw new TUnit.Core.Exceptions.SkipTestException(
				"LOOM_VULKAN_DIR / LOOM_DX12_DIR are not set — run tools/golden.ps1 to capture both backends, then this asserts they match.");

		var compared = 0;
		var failures = new List<string>();
		foreach (var vulkanPng in Directory.GetFiles(vulkanDir, "*.png"))
		{
			var name = Path.GetFileName(vulkanPng);
			var dx12Png = Path.Combine(dx12Dir, name);
			// A demo captured on one backend only (e.g. a DX-skipped shader on this toolchain) is not a parity
			// failure — there is simply nothing to compare it against.
			if (!File.Exists(dx12Png))
				continue;

			var (vw, vh, vulkanPixels) = PngReader.Read(vulkanPng);
			var (dw, dh, dx12Pixels) = PngReader.Read(dx12Png);
			if (vw != dw || vh != dh)
			{
				failures.Add($"{name}: size {vw}x{vh} (Vulkan) != {dw}x{dh} (DX12)");
				continue;
			}

			var diff = ImageComparison.Compare(vw, vh, vulkanPixels, dx12Pixels, PerChannelTolerance);
			compared++;
			if (diff.FailedFraction > MaxFailedFraction)
				failures.Add($"{name}: {diff}");
		}

		// Guard against a vacuous pass: if the dirs existed but no demo was captured on both, nothing was checked.
		compared.ShouldBeGreaterThan(0, "no demo was captured on both backends — nothing was compared for parity.");
		failures.ShouldBeEmpty("Cross-backend (Vulkan vs DX12) parity failures:\n  " + string.Join("\n  ", failures));
	}
}
