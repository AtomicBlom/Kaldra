using BinaryVibrance.Kaldra.Assets;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary><see cref="LayeredAssetSource"/>: ordered-root resolution (the layering the shader hot-reload watcher
/// resolves through). The debounced <see cref="AssetWatcher"/> is verified live (FileSystemWatcher timing is too
/// flaky to unit-test reliably).</summary>
public class AssetSourceTests : IDisposable
{
	private readonly string _baseRoot = Directory.CreateTempSubdirectory("kaldra-base-").FullName;
	private readonly string _packRoot = Directory.CreateTempSubdirectory("kaldra-pack-").FullName;

	[Test]
	public void TryResolve_finds_a_file_in_the_only_root()
	{
		File.WriteAllText(Path.Combine(_baseRoot, "shader.spv"), "base");
		var source = new LayeredAssetSource(_baseRoot);

		source.TryResolve("shader.spv", out var path).ShouldBeTrue();
		File.ReadAllText(path).ShouldBe("base");
	}

	[Test]
	public void TryResolve_prefers_the_higher_priority_root()
	{
		// pack (listed first = higher priority) overrides base — the layering a resource pack would use.
		File.WriteAllText(Path.Combine(_baseRoot, "shader.spv"), "base");
		File.WriteAllText(Path.Combine(_packRoot, "shader.spv"), "pack");
		var source = new LayeredAssetSource(_packRoot, _baseRoot);

		source.TryResolve("shader.spv", out var path).ShouldBeTrue();
		File.ReadAllText(path).ShouldBe("pack");
	}

	[Test]
	public void TryResolve_falls_through_to_a_lower_root_when_the_higher_one_lacks_the_file()
	{
		File.WriteAllText(Path.Combine(_baseRoot, "only-in-base.spv"), "base");
		var source = new LayeredAssetSource(_packRoot, _baseRoot);

		source.TryResolve("only-in-base.spv", out var path).ShouldBeTrue();
		path.ShouldStartWith(_baseRoot);
	}

	[Test]
	public void TryResolve_returns_false_for_a_missing_file()
	{
		var source = new LayeredAssetSource(_packRoot, _baseRoot);
		source.TryResolve("nope.spv", out var path).ShouldBeFalse();
		path.ShouldBe(string.Empty);
	}

	public void Dispose()
	{
		try { Directory.Delete(_baseRoot, recursive: true); } catch { /* best effort */ }
		try { Directory.Delete(_packRoot, recursive: true); } catch { /* best effort */ }
	}
}
