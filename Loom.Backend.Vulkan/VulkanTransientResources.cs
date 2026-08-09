using System.Diagnostics;
using Loom.Core.Graph;
using Loom.Graph;
using Silk.NET.Vulkan;
using VMASharp;
using Buffer = Silk.NET.Vulkan.Buffer;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Loom.Backend.Vulkan;

/// <summary>
/// The concrete GPU images backing a graph's resources, allocated through VMA. One <see cref="Image"/> +
/// view is created per non-backbuffer colour/depth resource, sized by its domain; the
/// backbuffer resource is a façade over whichever swapchain image was acquired this frame
/// (<see cref="SetBackbuffer"/>). Usage flags are derived from how the graph uses each resource — sampled
/// targets get <see cref="ImageUsageFlags.SampledBit"/> so a later <c>.Samples</c> pass can read them.
/// <para>
/// <b>Multi-buffered per frame-in-flight.</b> Each transient gets one physical image <em>per in-flight frame
/// slot</em>, so a frame writing a transient can never race a still-executing earlier frame reading the same
/// image — this is what lets the executor run more than one frame in flight (the swapchain provides distinct
/// backbuffer images itself, so the backbuffer façade needs no per-slot copy beyond its per-frame handle).
/// </para>
/// <para>
/// <b>Transient aliasing.</b> The Execute layer's <see cref="AliasPlan"/> partitions the
/// transients into buckets whose members never coexist; this backend turns each bucket into <em>one</em> VMA
/// allocation (per slot) and binds every member to it, so e.g. the bloom mip chain reuses the G-buffer's
/// memory once lighting has consumed it. The combined block takes the max size/alignment and the intersection
/// of the members' memory-type bits (a bucket with no common type would split — not yet hit by render-target
/// transients, which share a type). The hand-off needs an <em>aliasing barrier</em>: that is the executor's
/// job (it makes each non-first member's <c>Undefined→…</c> birth transition wait on all prior work to the
/// shared memory). VulkanSwapchain-sized resources are rebuilt on resize, bucket-wise; fixed-domain images keep size.
/// </para>
/// </summary>
internal sealed unsafe class VulkanTransientResources : IDisposable
{
	internal struct ResourceImage
	{
		public Image Image;
		public ImageView View;
		public VkFormat Format;
		public ImageAspectFlags Aspect;
		public Extent2D Extent;
		public bool IsBackbuffer;
		public bool SwapchainSized;
		public ImageUsageFlags Usage;
		public SampleCountFlags Samples;
	}

	// One VMA block shared by a bucket's members (per frame slot). The members alias it — never live at once.
	private struct SlotBucket
	{
		public int[] MemberIds;
		public Allocation Allocation;
		public bool SwapchainSized;
	}

	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly RenderGraph _graph;
	private readonly AliasPlan _aliasPlan;
	private readonly VulkanMemoryAllocator _allocator;
	private readonly VkFormat _backbufferFormat;
	private readonly ResourceImage[][] _images; // [frameSlot][ResourceHandle.Id]
	private readonly SlotBucket[][] _buckets;    // [frameSlot][bucketOrdinal]
	private readonly List<(int Id, Image Image, ImageView View, Allocation Allocation)> _persistent = []; // one per Persistent resource, shared across slots
	private readonly Buffer[][] _buffers;        // [frameSlot][ResourceHandle.Id] — storage/vertex buffers (GPU particles)
	private readonly List<Allocation> _bufferAllocations = [];
	private readonly Buffer[][] _constantBuffers;        // [frameSlot][ResourceHandle.Id] — host-visible param buffers (IEngine.Write)
	private readonly Allocation?[][] _constantAllocations; // parallel to _constantBuffers, mapped on write
	private readonly Buffer[][] _storageBuffers;          // [frameSlot][ResourceHandle.Id] — host-visible app storage buffers (IEngine.Write span)
	private readonly Allocation?[][] _storageAllocations; // parallel to _storageBuffers, mapped on write
	private Extent2D _swapExtent;

	public VulkanTransientResources(VulkanContext ctx, RenderGraph graph, AliasPlan aliasPlan, VkFormat backbufferFormat, Extent2D swapExtent, int framesInFlight, VulkanMemoryAllocator allocator)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_graph = graph;
		_aliasPlan = aliasPlan;
		_backbufferFormat = backbufferFormat;
		_swapExtent = swapExtent;
		_allocator = allocator; // shared with meshes; owned + disposed by the executor

		var sampled = UsedWith(graph, AccessKind.Sample);
		var storage = UsedWith(graph, AccessKind.ReadWrite);
		var readback = graph.ReadbackTargets.Select(r => r.Id).ToHashSet(); // need TRANSFER_SRC for the copy-out
		_images = new ResourceImage[framesInFlight][];
		_buckets = new SlotBucket[framesInFlight][];
		_buffers = new Buffer[framesInFlight][];
		_constantBuffers = new Buffer[framesInFlight][];
		_constantAllocations = new Allocation?[framesInFlight][];
		_storageBuffers = new Buffer[framesInFlight][];
		_storageAllocations = new Allocation?[framesInFlight][];
		for (var slot = 0; slot < framesInFlight; slot++)
		{
			_images[slot] = new ResourceImage[graph.Resources.Length];
			_buffers[slot] = new Buffer[graph.Resources.Length];
			_constantBuffers[slot] = new Buffer[graph.Resources.Length];
			_constantAllocations[slot] = new Allocation?[graph.Resources.Length];
			_storageBuffers[slot] = new Buffer[graph.Resources.Length];
			_storageAllocations[slot] = new Allocation?[graph.Resources.Length];
			foreach (var resource in graph.Resources)
				InitMetadata(slot, resource, sampled, storage, readback);
			_buckets[slot] = AllocateBuckets(slot);
		}
		// Persistent resources are allocated once and shared by every slot (cross-frame history).
		AllocatePersistentResources(framesInFlight);
		// Storage/vertex buffers (GPU particles) — one per slot, multi-buffered like transient images.
		AllocateBuffers(framesInFlight);
		// App-writable parameter buffers (IEngine.Write) — one host-visible buffer per slot, never aliased.
		AllocateConstantBuffers(framesInFlight);
		// App-writable storage buffers (IEngine.Write span) — one host-visible buffer per slot, never aliased.
		AllocateStorageBuffers(framesInFlight);
	}

	// Sets a resource's format/aspect/usage/flags without creating the image — the bucket pass allocates.
	private void InitMetadata(int slot, ResourceHandle resource, HashSet<int> sampled, HashSet<int> storage, HashSet<int> readback)
	{
		var aspect = resource.Kind == ResourceKind.Depth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;
		ref var image = ref _images[slot][resource.Id];
		image.Aspect = aspect;
		image.Samples = SampleFlags(resource.Domain.Samples); // 1x for the backbuffer + most domains

		if (resource.IsBackbuffer)
		{
			// Filled in per frame from the acquired swapchain image; no owned allocation.
			image.IsBackbuffer = true;
			image.SwapchainSized = true;
			image.Format = _backbufferFormat;
			return;
		}

		var usage = aspect == ImageAspectFlags.DepthBit
			? ImageUsageFlags.DepthStencilAttachmentBit
			: ImageUsageFlags.ColorAttachmentBit;
		if (sampled.Contains(resource.Id))
			usage |= ImageUsageFlags.SampledBit;
		if (storage.Contains(resource.Id))
			usage |= ImageUsageFlags.StorageBit; // a compute pass read-writes it (auto-exposure)
		if (readback.Contains(resource.Id))
			usage |= ImageUsageFlags.TransferSrcBit; // copied to host-visible memory for g.Readback (picking)

		image.Format = VulkanFormatMapping.ToVulkan(resource.Format);
		image.Usage = usage;
		// Backbuffer-domain and backbuffer-relative resources track the swapchain extent, so they're rebuilt on resize.
		image.SwapchainSized = resource.Domain.IsBackbuffer || resource.Domain.RelativeScale > 0f;
	}

	// Group the owned (non-backbuffer) resources by their alias bucket and back each bucket with one VMA
	// block. A resource the AliasPlan doesn't bucket (e.g. a future Persistent) gets its own singleton block.
	private SlotBucket[] AllocateBuckets(int slot)
	{
		var groups = new Dictionary<int, List<int>>();
		var next = -1; // synthetic keys for un-bucketed (dedicated) resources
		foreach (var resource in _graph.Resources)
		{
			if (resource.IsBackbuffer || resource.Kind is ResourceKind.Persistent or ResourceKind.Buffer or ResourceKind.Constants)
				continue; // backbuffer is the swapchain's; persistent shared across slots; buffers/constants aren't images
			var key = _aliasPlan.BucketOf(resource) ?? next--;
			(groups.TryGetValue(key, out var list) ? list : groups[key] = []).Add(resource.Id);
		}

		var buckets = new SlotBucket[groups.Count];
		var ordinal = 0;
		foreach (var members in groups.Values)
			buckets[ordinal++] = AllocateBucket(slot, [.. members]);
		return buckets;
	}

	// Create every member image, size one block to fit them all (max size/alignment, common memory type),
	// then bind each member to it so they share the memory. Views come after binding.
	private SlotBucket AllocateBucket(int slot, int[] memberIds)
	{
		ulong size = 0;
		ulong alignment = 0;
		var typeBits = uint.MaxValue;
		var swapchainSized = false;

		foreach (var id in memberIds)
		{
			ref var image = ref _images[slot][id];
			CreateImage(ref image, ExtentOf(_graph.Resources[id]));
			_vk.GetImageMemoryRequirements(_ctx.Device, image.Image, out var req);
			size = Math.Max(size, req.Size);
			alignment = Math.Max(alignment, req.Alignment);
			typeBits &= req.MemoryTypeBits;
			swapchainSized |= image.SwapchainSized;
		}

		if (typeBits == 0)
			throw new VulkanException("Alias bucket members share no common memory type; bucket splitting is not yet implemented.");

		MemoryRequirements combined = new() { Size = size, Alignment = alignment, MemoryTypeBits = typeBits };
		var allocation = _allocator.AllocateMemory(in combined, new AllocationCreateInfo { Usage = MemoryUsage.GPU_Only });

		foreach (var id in memberIds)
		{
			ref var image = ref _images[slot][id];
			allocation.BindImageMemory(image.Image).Check("vmaBindImageMemory");
			CreateView(ref image);
			NameImage(slot, id, in image);
		}

		return new SlotBucket { MemberIds = memberIds, Allocation = allocation, SwapchainSized = swapchainSized };
	}

	// Name a resource's image + view for GPU debuggers (RenderDoc), keyed by the graph resource's logical name
	// with a per-slot suffix to distinguish the frames-in-flight copies. [Conditional]: the calls and the name
	// interpolation compile to nothing without UseNamedGPUObjects.
	[Conditional("LOOM_NAMED_GPU_OBJECTS")]
	private void NameImage(int slot, int id, in ResourceImage image)
	{
		var name = _graph.Resources[id].Name;
		_ctx.SetObjectName(ObjectType.Image, image.Image.Handle, $"{name}#{slot}");
		_ctx.SetObjectName(ObjectType.ImageView, image.View.Handle, $"{name}.view#{slot}");
	}

	/// <summary>The backing image/view/format/extent for a resource in the given frame slot.</summary>
	public ref ResourceImage Get(int frameSlot, int id) => ref _images[frameSlot][id];

	/// <summary>The backing storage/vertex buffer for a <see cref="ResourceKind.Buffer"/> resource in the given slot.</summary>
	public Buffer GetBuffer(int frameSlot, int id) => _buffers[frameSlot][id];

	/// <summary>The backing host-visible parameter buffer for a <see cref="ResourceKind.Constants"/> resource in the given slot.</summary>
	public Buffer GetConstantBuffer(int frameSlot, int id) => _constantBuffers[frameSlot][id];

	/// <summary>Copy <paramref name="data"/> (the CPU shadow) into a parameter buffer's slot at frame start, before recording.</summary>
	public void WriteConstants(int frameSlot, int id, ReadOnlySpan<byte> data)
	{
		var allocation = _constantAllocations[frameSlot][id];
		if (allocation is null || data.Length == 0)
			return;
		var mapped = (byte*)allocation.Map();
		data.CopyTo(new Span<byte>(mapped, data.Length));
		allocation.Unmap();
	}

	// One host-visible UNIFORM buffer per Constants resource per frame slot (multi-buffered: this frame's CPU write
	// never races an earlier frame still reading its own slot's copy). Mapped on write; bound at offset 0.
	private void AllocateConstantBuffers(int framesInFlight)
	{
		foreach (var resource in _graph.Resources)
		{
			if (resource.Kind != ResourceKind.Constants)
				continue;
			BufferCreateInfo info = new()
			{
				SType = StructureType.BufferCreateInfo,
				Size = (ulong)resource.ByteSize,
				Usage = BufferUsageFlags.UniformBufferBit,
				SharingMode = SharingMode.Exclusive,
			};
			for (var slot = 0; slot < framesInFlight; slot++)
			{
				_constantBuffers[slot][resource.Id] = _allocator.CreateBuffer(in info, new AllocationCreateInfo { Usage = MemoryUsage.CPU_Only }, out var allocation);
				_constantAllocations[slot][resource.Id] = allocation;
				_ctx.SetObjectName(ObjectType.Buffer, _constantBuffers[slot][resource.Id].Handle, $"{resource.Name}#{slot}");
			}
		}
	}

	/// <summary>The backing host-visible storage buffer for a <see cref="ResourceKind.Storage"/> resource in the given slot.</summary>
	public Buffer GetStorageBuffer(int frameSlot, int id) => _storageBuffers[frameSlot][id];

	/// <summary>Copy <paramref name="data"/> (the CPU shadow) into a storage buffer's slot at frame start, before recording.</summary>
	public void WriteStorage(int frameSlot, int id, ReadOnlySpan<byte> data)
	{
		var allocation = _storageAllocations[frameSlot][id];
		if (allocation is null || data.Length == 0)
			return;
		var mapped = (byte*)allocation.Map();
		data.CopyTo(new Span<byte>(mapped, data.Length));
		allocation.Unmap();
	}

	// One host-visible STORAGE buffer per Storage resource per frame slot (multi-buffered like the parameter buffers).
	private void AllocateStorageBuffers(int framesInFlight)
	{
		foreach (var resource in _graph.Resources)
		{
			if (resource.Kind != ResourceKind.Storage)
				continue;
			BufferCreateInfo info = new()
			{
				SType = StructureType.BufferCreateInfo,
				Size = (ulong)resource.ByteSize,
				Usage = BufferUsageFlags.StorageBufferBit,
				SharingMode = SharingMode.Exclusive,
			};
			for (var slot = 0; slot < framesInFlight; slot++)
			{
				_storageBuffers[slot][resource.Id] = _allocator.CreateBuffer(in info, new AllocationCreateInfo { Usage = MemoryUsage.CPU_Only }, out var allocation);
				_storageAllocations[slot][resource.Id] = allocation;
				_ctx.SetObjectName(ObjectType.Buffer, _storageBuffers[slot][resource.Id].Handle, $"{resource.Name}#{slot}");
			}
		}
	}

	/// <summary>Reallocate a growable storage resource's per-slot buffers to <paramref name="newByteSize"/> bytes. The
	/// caller has drained the GPU, so freeing and recreating in place is safe; the next frame re-binds the new buffers
	/// (each draw fetches the storage buffer afresh) and re-uploads the enlarged shadow.</summary>
	public void GrowStorage(int id, int newByteSize)
	{
		BufferCreateInfo info = new()
		{
			SType = StructureType.BufferCreateInfo,
			Size = (ulong)newByteSize,
			Usage = BufferUsageFlags.StorageBufferBit,
			SharingMode = SharingMode.Exclusive,
		};
		for (var slot = 0; slot < _storageBuffers.Length; slot++)
		{
			if (_storageBuffers[slot][id].Handle != 0)
				_vk.DestroyBuffer(_ctx.Device, _storageBuffers[slot][id], null);
			_storageAllocations[slot][id]?.Dispose();
			_storageBuffers[slot][id] = _allocator.CreateBuffer(in info, new AllocationCreateInfo { Usage = MemoryUsage.CPU_Only }, out var allocation);
			_storageAllocations[slot][id] = allocation;
			_ctx.SetObjectName(ObjectType.Buffer, _storageBuffers[slot][id].Handle, $"{_graph.Resources[id].Name}#{slot}");
		}
	}

	// One device-local buffer per Buffer resource per frame slot (multi-buffered: a frame's compute writes it
	// while an earlier frame's draw may still read its own slot's copy). Usage covers storage (compute write)
	// + vertex (draw read) — the GPU particle path.
	private void AllocateBuffers(int framesInFlight)
	{
		foreach (var resource in _graph.Resources)
		{
			if (resource.Kind != ResourceKind.Buffer)
				continue;
			BufferCreateInfo info = new()
			{
				SType = StructureType.BufferCreateInfo,
				Size = (ulong)resource.ByteSize,
				Usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit,
				SharingMode = SharingMode.Exclusive,
			};
			for (var slot = 0; slot < framesInFlight; slot++)
			{
				_buffers[slot][resource.Id] = _allocator.CreateBuffer(in info, new AllocationCreateInfo { Usage = MemoryUsage.GPU_Only }, out var allocation);
				_bufferAllocations.Add(allocation);
				_ctx.SetObjectName(ObjectType.Buffer, _buffers[slot][resource.Id].Handle, $"{resource.Name}#{slot}");
			}
		}
	}

	/// <summary>Point the frame slot's backbuffer resource at the swapchain image acquired this frame.</summary>
	public void SetBackbuffer(int frameSlot, Image image, ImageView view, Extent2D extent)
	{
		var slot = _images[frameSlot];
		for (var i = 0; i < slot.Length; i++)
		{
			if (!slot[i].IsBackbuffer)
				continue;
			slot[i].Image = image;
			slot[i].View = view;
			slot[i].Extent = extent;
		}
	}

	/// <summary>
	/// Rebuild swapchain-sized images (all slots) after a resize; fixed-domain ones are untouched. Returns the ids of
	/// the <see cref="ResourceKind.Persistent"/> resources this reallocated — the caller must forget the layout it was
	/// tracking for each, since a brand-new image starts <see cref="ResourceState.Undefined"/> and a barrier claiming
	/// the old layout would be a lie about a different image.
	/// </summary>
	public IReadOnlyList<int> Recreate(Extent2D swapExtent)
	{
		_swapExtent = swapExtent;
		for (var slot = 0; slot < _buckets.Length; slot++)
			for (var b = 0; b < _buckets[slot].Length; b++)
			{
				ref var bucket = ref _buckets[slot][b];
				if (!bucket.SwapchainSized)
					continue;
				foreach (var id in bucket.MemberIds)
					DestroyImage(ref _images[slot][id]);
				bucket.Allocation.Dispose();
				_buckets[slot][b] = AllocateBucket(slot, bucket.MemberIds);
			}

		// Persistents live outside the buckets, so the loop above can't reach them — but a persistent anchored to the
		// backbuffer (or a backbuffer-relative) domain is swapchain-sized like any other target, and keeping the old
		// extent would leave it bound alongside resized attachments and sampled at the wrong size forever. Reallocate
		// it at the new extent; its contents are lost, which is the documented cost of resizing a screen-sized history
		// buffer (there is no resample). A fixed-domain persistent (the 1×1 exposure meter) is left completely alone,
		// so the history that actually can survive a resize does.
		var reset = new List<int>();
		foreach (var resource in _graph.Resources)
		{
			if (resource.Kind != ResourceKind.Persistent || !_images[0][resource.Id].SwapchainSized)
				continue;
			DestroyPersistent(resource.Id);
			CreatePersistent(resource, _images.Length);
			reset.Add(resource.Id);
		}
		return reset;
	}

	// Marshal the neutral resource sizing to a Vulkan extent. The scaling rules (backbuffer × own scale,
	// relative-domain fraction, fixed size) live on ResourceHandle so both backends size identically.
	private Extent2D ExtentOf(ResourceHandle resource)
	{
		var (w, h) = resource.Resolve((int)_swapExtent.Width, (int)_swapExtent.Height);
		return new Extent2D((uint)w, (uint)h);
	}

	// A domain's MSAA sample count (a power of two) mapped to the Vulkan flag. 1x for the backbuffer and most
	// domains; >1 makes the domain's targets multisampled (resolve to single-sample is handled in the MSAA path).
	private static SampleCountFlags SampleFlags(int count) => count switch
	{
		>= 16 => SampleCountFlags.Count16Bit,
		8 => SampleCountFlags.Count8Bit,
		4 => SampleCountFlags.Count4Bit,
		2 => SampleCountFlags.Count2Bit,
		_ => SampleCountFlags.Count1Bit,
	};

	// Raw image creation (no memory bound yet — the bucket binds it). Sets the image handle and extent.
	private void CreateImage(ref ResourceImage image, Extent2D extent)
	{
		image.Extent = extent;
		ImageCreateInfo info = new()
		{
			SType = StructureType.ImageCreateInfo,
			ImageType = ImageType.Type2D,
			Format = image.Format,
			Extent = new Extent3D(extent.Width, extent.Height, 1),
			MipLevels = 1,
			ArrayLayers = 1,
			Samples = image.Samples,
			Tiling = ImageTiling.Optimal,
			Usage = image.Usage,
			SharingMode = SharingMode.Exclusive,
			InitialLayout = ImageLayout.Undefined,
		};
		_vk.CreateImage(_ctx.Device, in info, null, out image.Image).Check("vkCreateImage");
	}

	private void CreateView(ref ResourceImage image)
	{
		ImageViewCreateInfo viewInfo = new()
		{
			SType = StructureType.ImageViewCreateInfo,
			Image = image.Image,
			ViewType = ImageViewType.Type2D,
			Format = image.Format,
			SubresourceRange = new ImageSubresourceRange(image.Aspect, 0, 1, 0, 1),
		};
		_vk.CreateImageView(_ctx.Device, in viewInfo, null, out image.View).Check("vkCreateImageView");
	}

	private void DestroyImage(ref ResourceImage image)
	{
		if (image.View.Handle != 0)
			_vk.DestroyImageView(_ctx.Device, image.View, null);
		if (image.Image.Handle != 0)
			_vk.DestroyImage(_ctx.Device, image.Image, null);
		image.View = default;
		image.Image = default;
	}

	// One image + memory per Persistent resource, shared by every frame slot so its contents carry across
	// frames (auto-exposure history). Allocated apart from the per-slot alias buckets, never recycled.
	private void AllocatePersistentResources(int framesInFlight)
	{
		foreach (var resource in _graph.Resources)
			if (resource.Kind == ResourceKind.Persistent)
				CreatePersistent(resource, framesInFlight);
	}

	// Create one Persistent resource's shared image + dedicated allocation at the current extent, and point every
	// frame slot's entry at it. Also the resize path, so a persistent is recreated exactly the way it was born.
	private void CreatePersistent(ResourceHandle resource, int framesInFlight)
	{
		ref var image = ref _images[0][resource.Id]; // metadata (format/usage/aspect) already set per slot
		CreateImage(ref image, ExtentOf(resource));
		_vk.GetImageMemoryRequirements(_ctx.Device, image.Image, out var req);
		MemoryRequirements reqs = new() { Size = req.Size, Alignment = req.Alignment, MemoryTypeBits = req.MemoryTypeBits };
		var allocation = _allocator.AllocateMemory(in reqs, new AllocationCreateInfo { Usage = MemoryUsage.GPU_Only });
		allocation.BindImageMemory(image.Image).Check("vmaBindImageMemory");
		CreateView(ref image);
		NameImage(0, resource.Id, in image); // shared across slots; named once
		_persistent.Add((resource.Id, image.Image, image.View, allocation));

		// Point every slot's entry at the one shared image/view.
		for (var slot = 0; slot < framesInFlight; slot++)
		{
			_images[slot][resource.Id].Image = image.Image;
			_images[slot][resource.Id].View = image.View;
			_images[slot][resource.Id].Extent = image.Extent;
		}
	}

	// Release one Persistent resource's shared image, view and dedicated allocation, and clear every slot's entry.
	private void DestroyPersistent(int id)
	{
		for (var i = _persistent.Count - 1; i >= 0; i--)
		{
			var (owned, image, view, allocation) = _persistent[i];
			if (owned != id)
				continue;
			if (view.Handle != 0)
				_vk.DestroyImageView(_ctx.Device, view, null);
			if (image.Handle != 0)
				_vk.DestroyImage(_ctx.Device, image, null);
			allocation.Dispose();
			_persistent.RemoveAt(i);
		}
		for (var slot = 0; slot < _images.Length; slot++)
		{
			_images[slot][id].Image = default;
			_images[slot][id].View = default;
		}
	}

	// Resources touched with the given access kind anywhere in the graph (drives usage flags at alloc time).
	private static HashSet<int> UsedWith(RenderGraph graph, AccessKind kind)
	{
		var ids = new HashSet<int>();
		foreach (var pass in graph.Passes)
			foreach (var access in pass.Accesses)
				if (access.Kind == kind)
					ids.Add(access.Resource.Id);
		return ids;
	}

	public void Dispose()
	{
		for (var slot = 0; slot < _buckets.Length; slot++)
			foreach (var bucket in _buckets[slot])
			{
				foreach (var id in bucket.MemberIds)
					DestroyImage(ref _images[slot][id]);
				bucket.Allocation.Dispose(); // returns the bucket's block to VMA
			}
		// Persistent images are shared across slots, so destroy each exactly once here.
		foreach (var (_, image, view, allocation) in _persistent)
		{
			if (view.Handle != 0)
				_vk.DestroyImageView(_ctx.Device, view, null);
			if (image.Handle != 0)
				_vk.DestroyImage(_ctx.Device, image, null);
			allocation.Dispose();
		}
		// Storage/vertex buffers: destroy each VkBuffer, then return its allocation to VMA.
		foreach (var slot in _buffers)
			foreach (var buffer in slot)
				if (buffer.Handle != 0)
					_vk.DestroyBuffer(_ctx.Device, buffer, null);
		foreach (var allocation in _bufferAllocations)
			allocation.Dispose();
		// Parameter buffers: one VkBuffer + allocation per slot per Constants resource.
		for (var slot = 0; slot < _constantBuffers.Length; slot++)
			for (var id = 0; id < _constantBuffers[slot].Length; id++)
			{
				if (_constantBuffers[slot][id].Handle != 0)
					_vk.DestroyBuffer(_ctx.Device, _constantBuffers[slot][id], null);
				_constantAllocations[slot][id]?.Dispose();
			}
		// Storage buffers: one VkBuffer + allocation per slot per Storage resource.
		for (var slot = 0; slot < _storageBuffers.Length; slot++)
			for (var id = 0; id < _storageBuffers[slot].Length; id++)
			{
				if (_storageBuffers[slot][id].Handle != 0)
					_vk.DestroyBuffer(_ctx.Device, _storageBuffers[slot][id], null);
				_storageAllocations[slot][id]?.Dispose();
			}
		// The allocator is owned by the executor (shared with meshes), so it is not disposed here.
	}
}
