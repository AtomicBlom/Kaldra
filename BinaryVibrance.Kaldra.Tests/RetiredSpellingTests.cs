namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// Guards against a rename half-landing. When a public name changes, the code is updated by the compiler and the
/// prose is updated by whoever remembers — and repeatedly, over this repository's history, the prose has not been:
/// <c>Engine.AvailableEngines</c> outlived its rename in five documents, two MSBuild files and two csproj comments;
/// the backend-project rename was finished in the tree while <c>CLAUDE.md</c> and <c>Planning.md</c> still described
/// it as pending; CI referenced two test classes that no longer existed.
/// <para>
/// So each retired spelling gets an entry here and the build refuses to let it back in. This costs one line per
/// rename and replaces "grep the docs and hope", which is the process that produced the list above.
/// </para>
/// <para>
/// The scan covers prose and build files as well as source, because that is where the misses actually were — a
/// <c>.targets</c> comment is documentation as surely as a <c>.md</c> file, and nothing was checking it.
/// </para>
/// <para>
/// A line that names a retired spelling on purpose — a doc saying a flag was removed has to name the flag to be
/// useful — carries <c>KALDRA-RETIRED-OK</c> to say so.
/// </para>
/// </summary>
public class RetiredSpellingTests
{
	private readonly record struct Retired(string Spelling, string Replacement);

	// One entry per rename that has already been made. The replacement is quoted in the failure so whoever trips it
	// does not have to go digging for what the new name is.
	private static readonly Retired[] Retirements =
	[
		new("AvailableEngines", "Backends.Available"),
		new("KaldraApplication.Draw", "KaldraApplication.BeforeRender"),
		new("ctx.DrawInstanced", "ctx.Draw with a DrawCall.Instances source"),
		new("SetUniforms<T>(in T)", "SetUniforms<T>(ref T)"),
		new("SetUniforms{T}(in T)", "SetUniforms{T}(ref T)"),   // the XML-doc cref spelling of the same thing
		new("GoldenImageTests", "GalleryGoldenTests"),
		new("CrossBackendParityTests", "GalleryParityTests"),
		new("KALDRA_GOLDEN_DIR", "KALDRA_GOLDEN_UPDATE"),
		new("--smoke-frames", "the in-process offscreen tests (there is no capture flag)"),
		new("BinaryVibrance.Kaldra.Vulkan", "BinaryVibrance.Kaldra.Backend.Vulkan"),
		new("BinaryVibrance.Kaldra.DirectX12", "BinaryVibrance.Kaldra.Backend.DirectX12"),
		new("DirectX12Shaders.Builtins", "DirectX12Shaders.TryResolveStageDxil — an unresolvable shader throws rather than substituting one"),
		new("DirectX12Triangle", "the graph path; a no-graph Render throws on every backend"),
		new("RenderLegacy", "RenderGraphFrame; a no-graph Render throws on every backend"),
	];

	// Put this on a line to say "the mention is deliberate" — a doc that tells the reader a flag was removed has to
	// name the flag to be useful. Without an escape hatch the guard cries wolf on exactly those lines, and a guard
	// that cries wolf gets deleted.
	private const string DeliberateMention = "KALDRA-RETIRED-OK";

	private static readonly string[] ScannedExtensions =
		[".cs", ".md", ".slang", ".csproj", ".props", ".targets", ".yml", ".yaml", ".txt", ".slnx"];

	// Dated, HEAD-pinned review snapshots. Their findings and file:line references describe the tree as it was at a
	// named commit, so rewriting the names in them would falsify a historical record rather than fix drift.
	private static readonly string[] HistoricalRecords =
		["REVIEW-2026-07.md", "CODE_REVIEW.md", "COMMENT_REVIEW.md"];

	[Test]
	public void No_retired_spelling_has_come_back()
	{
		var root = RepositoryRoot();
		var offences = new List<string>();
		var scanned = 0;

		foreach (var file in EnumerateScannableFiles(root))
		{
			scanned++;
			var lines = File.ReadAllLines(file);
			for (var i = 0; i < lines.Length; i++)
			{
				if (lines[i].Contains(DeliberateMention, StringComparison.Ordinal))
					continue;
				foreach (var retired in Retirements)
					if (lines[i].Contains(retired.Spelling, StringComparison.Ordinal))
						offences.Add($"{Path.GetRelativePath(root, file)}:{i + 1}  '{retired.Spelling}' — use {retired.Replacement}");
			}
		}

		// Guard against a vacuous pass: a broken root path or an over-eager exclusion would scan nothing and report
		// success. The repository has hundreds of matching files; a handful means the walk is wrong.
		scanned.ShouldBeGreaterThan(100, "the scan found almost no files — the repository root or the filter is wrong, so a clean result means nothing.");

		offences.ShouldBeEmpty(
			$"A renamed name has come back (scanned {scanned} files). Update the text, or if the name is being " +
			$"un-retired, remove its entry from {nameof(RetiredSpellingTests)}:\n  " + string.Join("\n  ", offences));
	}

	private static IEnumerable<string> EnumerateScannableFiles(string root)
	{
		foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
		{
			var relative = Path.GetRelativePath(root, file);
			// Build output, git internals, and the vendored submodules (not ours to rename).
			if (relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
				|| relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
				|| relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
				|| relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
				|| relative.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
				|| relative.StartsWith("external" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
				continue;

			var name = Path.GetFileName(file);
			// This file lists every retired spelling by definition, and the historical records preserve them on purpose.
			if (name == $"{nameof(RetiredSpellingTests)}.cs" || HistoricalRecords.Contains(name))
				continue;
			if (!ScannedExtensions.Contains(Path.GetExtension(file)))
				continue;

			yield return file;
		}
	}

	// bin/<config>/<tfm> → up three to the project, up one more to the repository root.
	private static string RepositoryRoot() =>
		Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
