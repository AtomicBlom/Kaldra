using Loom.Core;
using Loom.Graph;
using Silk.NET.Vulkan;
using VMASharp;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Loom.Vulkan;

/// <summary>
/// The concrete GPU images backing a graph's resources, allocated through VMA. One <see cref="Image"/> +
/// view is created per non-backbuffer colour/depth resource, sized by its domain (Planning.md §6/§9); the
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
/// <b>Transient aliasing (§8, §12 M4).</b> The Execute layer's <see cref="AliasPlan"/> partitions the
/// transients into buckets whose members never coexist; this backend turns each bucket into <em>one</em> VMA
/// allocation (per slot) and binds every member to it, so e.g. the bloom mip chain reuses the G-buffer's
/// memory once lighting has consumed it. The combined block takes the max size/alignment and the intersection
/// of the members' memory-type bits (a bucket with no common type would split — not yet hit by render-target
/// transients, which share a type). The hand-off needs an <em>aliasing barrier</em>: that is the executor's
/// job (it makes each non-first member's <c>Undefined→…</c> birth transition wait on all prior work to the
/// shared memory). Swapchain-sized resources are rebuilt on resize, bucket-wise; fixed-domain images keep size.
/// </para>
/// </summary>
internal sealed unsafe class TransientResources : IDisposable
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
	private Extent2D _swapExtent;

	public TransientResources(VulkanContext ctx, RenderGraph graph, AliasPlan aliasPlan, VkFormat backbufferFormat, Extent2D swapExtent, int framesInFlight, VulkanMemoryAllocator allocator)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_graph = graph;
		_aliasPlan = aliasPlan;
		_backbufferFormat = backbufferFormat;
		_swapExtent = swapExtent;
		_allocator = allocator; // shared with meshes; owned + disposed by the executor

		var sampled = SampledResourceIds(graph);
		_images = new ResourceImage[framesInFlight][];
		_buckets = new SlotBucket[framesInFlight][];
		for (var slot = 0; slot < framesInFlight; slot++)
		{
			_images[slot] = new ResourceImage[graph.Resources.Length];
			foreach (var resource in graph.Resources)
				InitMetadata(slot, resource, sampled);
			_buckets[slot] = AllocateBuckets(slot);
		}
	}

	// Sets a resource's format/aspect/usage/flags without creating the image — the bucket pass allocates.
	private void InitMetadata(int slot, ResourceHandle resource, HashSet<int> sampled)
	{
		var aspect = resource.Kind == ResourceKind.Depth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;
		ref var image = ref _images[slot][resource.Id];
		image.Aspect = aspect;

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

		image.Format = FormatMapping.ToVulkan(resource.Format);
		image.Usage = usage;
		image.SwapchainSized = resource.Domain.IsBackbuffer;
	}

	// Group the owned (non-backbuffer) resources by their alias bucket and back each bucket with one VMA
	// block. A resource the AliasPlan doesn't bucket (e.g. a future Persistent) gets its own singleton block.
	private SlotBucket[] AllocateBuckets(int slot)
	{
		var groups = new Dictionary<int, List<int>>();
		var next = -1; // synthetic keys for un-bucketed (dedicated) resources
		foreach (var resource in _graph.Resources)
		{
			if (resource.IsBackbuffer)
				continue;
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
		}

		return new SlotBucket { MemberIds = memberIds, Allocation = allocation, SwapchainSized = swapchainSized };
	}

	/// <summary>The backing image/view/format/extent for a resource in the given frame slot.</summary>
	public ref ResourceImage Get(int frameSlot, int id) => ref _images[frameSlot][id];

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

	/// <summary>Rebuild swapchain-sized buckets (all slots) after a resize; fixed-domain buckets are untouched.</summary>
	public void Recreate(Extent2D swapExtent)
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
	}

	// Backbuffer-domain resources follow the native swapchain extent; fixed domains use their declared size.
	private Extent2D ExtentOf(ResourceHandle resource)
	{
		if (resource.Domain.IsBackbuffer)
			return _swapExtent;
		var (w, h) = resource.Resolve();
		return new Extent2D((uint)w, (uint)h);
	}

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
			Samples = SampleCountFlags.Count1Bit,
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

	// Resources read via .Samples anywhere in the graph need SampledBit at allocation time.
	private static HashSet<int> SampledResourceIds(RenderGraph graph)
	{
		var sampled = new HashSet<int>();
		foreach (var pass in graph.Passes)
			foreach (var access in pass.Accesses)
				if (access.Kind == AccessKind.Sample)
					sampled.Add(access.Resource.Id);
		return sampled;
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
		// The allocator is owned by the executor (shared with meshes), so it is not disposed here.
	}
}
