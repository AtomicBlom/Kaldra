namespace BinaryVibrance.Kaldra.Graph;

/// <summary>
/// Where a backend's persistent pipeline cache lives, and how it is written. Backend-neutral because the policy is:
/// the file is per user, per backend, and per device, and it is replaced atomically so a process killed mid-write
/// leaves the previous cache intact rather than a truncated one.
/// </summary>
public static class PipelineCacheFile
{
	/// <summary>
	/// The cache file for <paramref name="backend"/> on the device identified by <paramref name="deviceKey"/>, or null
	/// if no writable location can be determined. The device key belongs in the <em>name</em> rather than the contents
	/// so that moving a drive between machines, or switching GPUs, picks a different file instead of invalidating one.
	/// </summary>
	public static string? PathFor(string backend, string deviceKey)
	{
		try
		{
			var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			if (string.IsNullOrEmpty(root))
				return null;

			var directory = Path.Combine(root, "Kaldra", "PipelineCache");
			Directory.CreateDirectory(directory);
			return Path.Combine(directory, $"{backend}-{deviceKey}.bin");
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return null; // no cache rather than no engine
		}
	}

	/// <summary>
	/// Replace <paramref name="path"/> with <paramref name="data"/>, writing to a sibling temporary file first. A
	/// half-written cache is worse than none: the header would still look valid to the next run, so the driver would
	/// be handed a truncated blob.
	/// </summary>
	public static void Write(string path, byte[] data)
	{
		var temporary = path + ".tmp";
		File.WriteAllBytes(temporary, data);
		File.Move(temporary, path, overwrite: true);
	}
}
