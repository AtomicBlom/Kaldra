using Loom.Core;
using Silk.NET.Vulkan;
using VMASharp;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Loom.Vulkan;

/// <summary>
/// The concrete GPU images backing a graph's resources, allocated through VMA. One <see cref="Image"/> +
/// view + sub-allocation is created per non-backbuffer colour/depth resource, sized by its domain
/// (Planning.md §6/§9); the backbuffer resource is a façade over whichever swapchain image was acquired
/// this frame (<see cref="SetBackbuffer"/>). Usage flags are derived from how the graph uses each resource —
/// sampled targets get <see cref="ImageUsageFlags.SampledBit"/> so a later <c>.Samples</c> pass can read them.
/// <para>
/// <b>Multi-buffered per frame-in-flight.</b> Each transient gets one physical image <em>per in-flight frame
/// slot</em>, so a frame writing a transient can never race a still-executing earlier frame reading the same
/// image — this is what lets the executor run more than one frame in flight (the swapchain provides distinct
/// backbuffer images itself, so the backbuffer façade needs no per-slot copy beyond its per-frame handle).
/// No transient aliasing yet: within a slot, every resource has its own backing memory; the aliaser that
/// recycles non-overlapping lifetimes into shared memory is the next M4 step. Swapchain-sized resources
/// (those in the backbuffer domain) are rebuilt on resize; fixed-domain images keep their size.
/// </para>
/// </summary>
internal sealed unsafe class TransientResources : IDisposable
{
	internal struct ResourceImage
	{
		public Image Image;
		public ImageView View;
		public Allocation? Allocation;
		public VkFormat Format;
		public ImageAspectFlags Aspect;
		public Extent2D Extent;
		public bool IsBackbuffer;
		public bool SwapchainSized;
		public ImageUsageFlags Usage;
	}

	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly RenderGraph _graph;
	private readonly VulkanMemoryAllocator _allocator;
	private readonly VkFormat _backbufferFormat;
	private readonly ResourceImage[][] _images; // [frameSlot][ResourceHandle.Id]
	private Extent2D _swapExtent;

	public TransientResources(VulkanContext ctx, RenderGraph graph, VkFormat backbufferFormat, Extent2D swapExtent, int framesInFlight, VulkanMemoryAllocator allocator)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_graph = graph;
		_backbufferFormat = backbufferFormat;
		_swapExtent = swapExtent;
		_allocator = allocator; // shared with meshes; owned + disposed by the executor

		var sampled = SampledResourceIds(graph);
		_images = new ResourceImage[framesInFlight][];
		for (var slot = 0; slot < framesInFlight; slot++)
		{
			_images[slot] = new ResourceImage[graph.Resources.Length];
			foreach (var resource in graph.Resources)
				InitResource(slot, resource, sampled);
		}
	}

	private void InitResource(int slot, ResourceHandle resource, HashSet<int> sampled)
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
		Allocate(ref image, ExtentOf(resource));
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

	/// <summary>Rebuild swapchain-sized images (all slots) after a resize; fixed-domain images are untouched.</summary>
	public void Recreate(Extent2D swapExtent)
	{
		_swapExtent = swapExtent;
		foreach (var slot in _images)
			foreach (var resource in _graph.Resources)
			{
				ref var image = ref slot[resource.Id];
				if (image.IsBackbuffer || !image.SwapchainSized)
					continue;
				DestroyImage(ref image);
				Allocate(ref image, ExtentOf(resource));
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

	private void Allocate(ref ResourceImage image, Extent2D extent)
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
		// VMA creates the image and binds sub-allocated device-local memory in one call.
		image.Image = _allocator.CreateImage(in info, new AllocationCreateInfo { Usage = MemoryUsage.GPU_Only }, out image.Allocation);

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
		image.Allocation?.Dispose(); // returns the sub-allocation to the VMA block
		image.View = default;
		image.Image = default;
		image.Allocation = null;
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
		foreach (var slot in _images)
			for (var i = 0; i < slot.Length; i++)
				if (!slot[i].IsBackbuffer)
					DestroyImage(ref slot[i]);
		// The allocator is owned by the executor (shared with meshes), so it is not disposed here.
	}
}
