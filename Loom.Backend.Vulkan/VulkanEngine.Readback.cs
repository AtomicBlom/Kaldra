using Loom.Graph;
using Silk.NET.Vulkan;
using VMASharp;
using Buffer = Silk.NET.Vulkan.Buffer;
using GraphFormat = Loom.Core.Graph.Format;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Loom.Backend.Vulkan;

internal sealed unsafe partial class VulkanEngine
{
	// Copy each readback target into this slot's host-visible buffer. The slot's fence guards them; DownloadReadback
	// hands them to the app ~2 frames later. The pre-copy transition comes from the derived plan — the last graph use
	// may be a .Samples (SHADER_READ) or the .Writes that produced it (COLOR_ATTACHMENT), and only derivation knows
	// which; assuming either one here would emit a barrier with the wrong old layout for the other shape.
	private void RecordReadback(CommandBuffer cmd)
	{
		EmitTransitions(cmd, Barriers.ReadbackTransitions.AsSpan());

		var copyLayout = VulkanStateMapping.For(ResourceState.CopySource).Layout;
		foreach (var readback in _readbacks)
		{
			ref var image = ref _transients!.Get(_currentFrame, readback.Id);

			BufferImageCopy region = new()
			{
				ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
				ImageExtent = new Extent3D(image.Extent.Width, image.Extent.Height, 1),
			};
			_vk.CmdCopyImageToBuffer(cmd, image.Image, copyLayout, readback.Buffers[_currentFrame], 1, in region);
			readback.Valid[_currentFrame] = true;
		}
	}

	// Hand this slot's readback buffers to StoreReadback as raw, tightly-packed bytes (Vulkan's image→buffer copy
	// carries no row padding). Called after the slot's fence signals, so the data is the copy from when the slot
	// last ran (a frame or two ago); the app decodes whatever format it declared.
	private void DownloadReadback(int slot)
	{
		foreach (var readback in _readbacks)
		{
			if (!readback.Valid[slot])
				continue;
			var count = readback.ByteCount;
			var allocation = readback.Allocations[slot];
			var mapped = (void*)allocation.Map();
			StoreReadback(readback.Id, new ReadOnlySpan<byte>(mapped, count));
			allocation.Unmap();
		}
	}

	// Bytes per texel of a readback target's format — the stride StoreReadback hands the app, and the size of each
	// per-slot staging buffer. Extend as new readback formats appear (the picking id buffer is R16F).
	private static int BytesPerTexel(GraphFormat format) => format switch
	{
		GraphFormat.R16F => 2,
		GraphFormat.Rg16F => 4,
		GraphFormat.R32Uint => 4,
		GraphFormat.Rgba8Unorm or GraphFormat.Rgba8Srgb => 4,
		GraphFormat.Rgba16F => 8,
		_ => throw new NotSupportedException($"Vulkan: readback of format {format} is not supported."),
	};

	// Read this slot's timestamps (begin/end per pass) and decode them into _timings + the frame total. Called after
	// the slot's fence signals, so the queries are complete. Mask to the queue's valid bits before decoding; the
	// timestamp period is nanoseconds per tick, so ns/1e6 is the milliseconds-per-tick the decoder needs.
	private void DownloadTimings(int slot)
	{
		if (!GpuTimingEnabled || _timingPools.Length == 0 || _timingPassCount == 0 || !_timingValid[slot])
			return;

		var n = 2 * _timingPassCount;
		ulong* raw = stackalloc ulong[n];
		_vk.GetQueryPoolResults(_ctx.Device, _timingPools[slot], 0, (uint)n,
			(nuint)(n * sizeof(ulong)), raw, (ulong)sizeof(ulong), QueryResultFlags.Result64Bit).Check("vkGetQueryPoolResults");

		var mask = _timestampValidBits >= 64 ? ulong.MaxValue : (1UL << (int)_timestampValidBits) - 1UL;
		for (var i = 0; i < n; i++)
			raw[i] &= mask;
		DecodeTimings(new ReadOnlySpan<ulong>(raw, n), Barriers, _timestampPeriod / 1_000_000.0);
	}

	// Host-visible staging a GPU→CPU copy lands in. Allocated through VMA like every other buffer the backend owns:
	// a raw vkAllocateMemory per readback slot and per capture spends a device allocation from the driver's
	// maxMemoryAllocationCount — a budget as low as 4096 on some drivers — where VMA sub-allocates out of blocks it
	// already holds.
	private (Buffer Buffer, Allocation Allocation) CreateStagingBuffer(ulong size)
	{
		BufferCreateInfo info = new()
		{
			SType = StructureType.BufferCreateInfo,
			Size = size,
			Usage = BufferUsageFlags.TransferDstBit,
			SharingMode = SharingMode.Exclusive,
		};
		var buffer = _allocator.CreateBuffer(in info, new AllocationCreateInfo { Usage = MemoryUsage.GPU_To_CPU }, out var allocation);
		return (buffer, allocation);
	}

	// PNG capture only handles an 8-bit RGBA/BGRA present target (the formats WriteCapture decodes); an HDR
	// swapchain format (FP16 / 10-bit packed) is not capturable to PNG here.
	private bool CaptureSupported() =>
		_presentTarget.ImageFormat is VkFormat.B8G8R8A8Srgb or VkFormat.B8G8R8A8Unorm or VkFormat.R8G8B8A8Srgb or VkFormat.R8G8B8A8Unorm;

	private void WriteCapture(Allocation allocation, ulong size, uint width, uint height, string path)
	{
		var mapped = (void*)allocation.Map();
		var src = new ReadOnlySpan<byte>(mapped, (int)size);

		// The present target is typically B8G8R8A8; PNG wants RGBA, so swap R/B for those formats.
		var swizzle = _presentTarget.ImageFormat is VkFormat.B8G8R8A8Srgb or VkFormat.B8G8R8A8Unorm;
		var rgba = new byte[(int)size];
		for (var i = 0; i < (int)(width * height); i++)
		{
			var o = i * 4;
			rgba[o] = swizzle ? src[o + 2] : src[o];
			rgba[o + 1] = src[o + 1];
			rgba[o + 2] = swizzle ? src[o] : src[o + 2];
			rgba[o + 3] = src[o + 3];
		}
		allocation.Unmap();

		PngWriter.WriteRgba(path, (int)width, (int)height, rgba);
	}
}
