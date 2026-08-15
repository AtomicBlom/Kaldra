using Loom.Core.Diagnostics;
using Loom.Graph;
using Silk.NET.Vulkan;

namespace Loom.Backend.Vulkan;

/// <summary>
/// A <c>VkPipelineCache</c> that survives the process: the driver's compiled-pipeline blob is loaded at start-up and
/// written back on shutdown, so a second run of the same build skips most of the shader compilation a first run pays
/// for. The in-memory <see cref="VulkanTechniqueCache"/> stops a pipeline being baked twice in one run; this stops it
/// being compiled again in the next one.
/// <para>
/// <b>Never fatal.</b> A cache file is a hint, not an asset. Anything that can go wrong with it — missing, truncated,
/// written by another GPU or another driver version, unreadable directory, read-only disk — leaves the engine
/// starting with an empty cache and a log line, because the alternative is a corrupt file in a user's profile
/// directory bricking the application. The file is keyed by the device's own pipeline-cache UUID and its header is
/// checked against this device before use, so a stale blob is normally rejected here rather than at
/// <c>vkCreatePipelineCache</c>.
/// </para>
/// </summary>
internal sealed unsafe class VulkanPipelineCache : IDisposable
{
	// VkPipelineCacheHeaderVersionOne: headerSize, headerVersion, vendorID, deviceID, then the 16-byte UUID.
	private const int HeaderSize = 32;
	private const int UuidSize = 16;
	private const uint HeaderVersionOne = 1;

	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly string? _path;
	private PipelineCache _cache;

	public VulkanPipelineCache(VulkanContext ctx, bool persist)
	{
		_ctx = ctx;
		_vk = ctx.Vk;

		var properties = ctx.PhysicalDeviceProperties;
		var uuid = new byte[UuidSize];
		for (var i = 0; i < UuidSize; i++)
			uuid[i] = properties.PipelineCacheUuid[i];

		_path = persist ? PipelineCacheFile.PathFor("vulkan", Convert.ToHexString(uuid)) : null;
		var initial = _path is null ? null : Load(_path, properties, uuid);

		fixed (byte* pInitial = initial)
		{
			PipelineCacheCreateInfo info = new()
			{
				SType = StructureType.PipelineCacheCreateInfo,
				InitialDataSize = (nuint)(initial?.Length ?? 0),
				PInitialData = pInitial,
			};
			if (_vk.CreatePipelineCache(ctx.Device, in info, null, out _cache) != Result.Success)
			{
				// Even a header-checked blob can be rejected by the driver. Retry empty rather than fail the engine.
				LoomLog.Warning("Vulkan pipeline cache could not be created from the stored blob; starting empty.");
				PipelineCacheCreateInfo empty = new() { SType = StructureType.PipelineCacheCreateInfo };
				_vk.CreatePipelineCache(ctx.Device, in empty, null, out _cache).Check("vkCreatePipelineCache");
			}
		}
	}

	/// <summary>The handle to pass to <c>vkCreate*Pipelines</c>. The null handle when caching is off, which those
	/// calls accept as "no cache".</summary>
	public PipelineCache Handle => _cache;

	// Read the blob and check its header describes this device. The driver would reject a mismatch anyway, but doing
	// it here means a stale file is a log line rather than a silent no-op, and it is the documented portable check.
	private static byte[]? Load(string path, PhysicalDeviceProperties properties, byte[] uuid)
	{
		try
		{
			if (!File.Exists(path))
				return null;

			var data = File.ReadAllBytes(path);
			if (data.Length < HeaderSize)
			{
				LoomLog.Info("Vulkan pipeline cache file is too short to hold a header; ignoring it.");
				return null;
			}

			var headerVersion = BitConverter.ToUInt32(data, 4);
			var vendorId = BitConverter.ToUInt32(data, 8);
			var deviceId = BitConverter.ToUInt32(data, 12);
			if (headerVersion != HeaderVersionOne || vendorId != properties.VendorID || deviceId != properties.DeviceID)
			{
				LoomLog.Info("Vulkan pipeline cache file was written by a different device; ignoring it.");
				return null;
			}
			for (var i = 0; i < UuidSize; i++)
			{
				if (data[16 + i] == uuid[i])
					continue;
				LoomLog.Info("Vulkan pipeline cache file was written by a different driver version; ignoring it.");
				return null;
			}
			return data;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			LoomLog.Info($"Vulkan pipeline cache file could not be read ({e.Message}); starting empty.");
			return null;
		}
	}

	private void Save()
	{
		if (_path is null || _cache.Handle == 0)
			return;

		try
		{
			nuint size = 0;
			if (_vk.GetPipelineCacheData(_ctx.Device, _cache, ref size, null) != Result.Success || size == 0)
				return;

			var data = new byte[(int)size];
			fixed (byte* p = data)
			{
				if (_vk.GetPipelineCacheData(_ctx.Device, _cache, ref size, p) != Result.Success)
					return;
			}
			PipelineCacheFile.Write(_path, data);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			LoomLog.Info($"Vulkan pipeline cache could not be written ({e.Message}).");
		}
	}

	private static string UuidHex(ReadOnlySpan<byte> uuid) => Convert.ToHexString(uuid[..UuidSize]);

	public void Dispose()
	{
		Save();
		if (_cache.Handle != 0)
			_vk.DestroyPipelineCache(_ctx.Device, _cache, null);
		_cache = default;
	}
}
