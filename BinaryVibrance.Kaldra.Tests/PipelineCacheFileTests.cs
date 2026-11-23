using BinaryVibrance.Kaldra.Graph;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// The file half of the persistent pipeline caches. Device-free: what matters here is that a cache file is per
/// backend and per device, and that replacing one is atomic — a half-written blob still looks like a valid header to
/// the next run, so a process killed mid-write must leave the previous cache intact rather than a truncated one.
/// </summary>
public class PipelineCacheFileTests
{
	[Test]
	public void A_cache_path_is_distinct_per_backend_and_per_device()
	{
		var vulkan = PipelineCacheFile.PathFor("vulkan", "AAAA");
		var directx = PipelineCacheFile.PathFor("directx12", "AAAA");
		var otherDevice = PipelineCacheFile.PathFor("vulkan", "BBBB");

		vulkan.ShouldNotBeNull();
		directx.ShouldNotBeNull();
		otherDevice.ShouldNotBeNull();

		vulkan.ShouldNotBe(directx, "two backends sharing one cache file would hand each other's blobs over.");
		vulkan.ShouldNotBe(otherDevice, "one device's blobs must not be offered to another's driver.");
		Path.GetDirectoryName(vulkan).ShouldBe(Path.GetDirectoryName(directx), "both belong under the same cache directory.");
	}

	[Test]
	public void Writing_replaces_the_previous_file_whole()
	{
		var path = PipelineCacheFile.PathFor("test-backend", Guid.NewGuid().ToString("N"))!;
		try
		{
			PipelineCacheFile.Write(path, [1, 2, 3, 4, 5, 6, 7, 8]);
			File.ReadAllBytes(path).ShouldBe([1, 2, 3, 4, 5, 6, 7, 8]);

			// A shorter second write must not leave the tail of the first behind — that is exactly the truncated-blob
			// shape a driver would accept the header of and then choke on.
			PipelineCacheFile.Write(path, [9, 9]);
			File.ReadAllBytes(path).ShouldBe([9, 9]);

			// The temporary the write goes through must not survive it.
			File.Exists(path + ".tmp").ShouldBeFalse();
		}
		finally
		{
			if (File.Exists(path))
				File.Delete(path);
			if (File.Exists(path + ".tmp"))
				File.Delete(path + ".tmp");
		}
	}
}
