using Loom.Core;
using Silk.NET.Vulkan;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Loom.Vulkan;

/// <summary>
/// The concrete GPU images backing a graph's resources. One <see cref="Image"/> + view + memory is
/// allocated per non-backbuffer colour/depth resource, sized by its domain (Planning.md §6/§9); the
/// backbuffer resource is a façade over whichever swapchain image was acquired this frame
/// (<see cref="SetBackbuffer"/>). Usage flags are derived from how the graph uses each resource — sampled
/// targets get <see cref="ImageUsageFlags.SampledBit"/> so a later <c>.Samples</c> pass can read them.
/// <para>
/// No transient aliasing yet: every resource gets its own backing memory for the whole frame. The aliaser
/// that recycles non-overlapping lifetimes is M4. Resources whose size follows the swapchain (those in the
/// backbuffer domain) are rebuilt on resize; fixed-domain images keep their size.
/// </para>
/// </summary>
internal sealed unsafe class TransientResources : IDisposable
{
	internal struct ResourceImage
	{
		public Image Image;
		public ImageView View;
		public DeviceMemory Memory;
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
	private readonly ResourceImage[] _images; // indexed by ResourceHandle.Id
	private Extent2D _swapExtent;

	public TransientResources(VulkanContext ctx, RenderGraph graph, VkFormat backbufferFormat, Extent2D swapExtent)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_graph = graph;
		_swapExtent = swapExtent;
		_images = new ResourceImage[graph.Resources.Length];

		var sampled = SampledResourceIds(graph);

		foreach (var resource in graph.Resources)
		{
			var aspect = resource.Kind == ResourceKind.Depth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;
			ref var slot = ref _images[resource.Id];
			slot.Aspect = aspect;

			if (resource.IsBackbuffer)
			{
				// Filled in per frame from the acquired swapchain image; no owned allocation.
				slot.IsBackbuffer = true;
				slot.SwapchainSized = true;
				slot.Format = backbufferFormat;
				continue;
			}

			var usage = aspect == ImageAspectFlags.DepthBit
				? ImageUsageFlags.DepthStencilAttachmentBit
				: ImageUsageFlags.ColorAttachmentBit;
			if (sampled.Contains(resource.Id))
				usage |= ImageUsageFlags.SampledBit;

			slot.Format = FormatMapping.ToVulkan(resource.Format);
			slot.Usage = usage;
			slot.SwapchainSized = resource.Domain.IsBackbuffer;
			Allocate(ref slot, ExtentOf(resource));
		}
	}

	/// <summary>The backing image/view/format/extent for a resource (the backbuffer reflects the current frame).</summary>
	public ref ResourceImage Get(int id) => ref _images[id];

	/// <summary>Point the backbuffer resource at the swapchain image acquired this frame.</summary>
	public void SetBackbuffer(Image image, ImageView view, Extent2D extent)
	{
		for (var i = 0; i < _images.Length; i++)
		{
			if (!_images[i].IsBackbuffer)
				continue;
			_images[i].Image = image;
			_images[i].View = view;
			_images[i].Extent = extent;
		}
	}

	/// <summary>Rebuild swapchain-sized images after a resize; fixed-domain images are untouched.</summary>
	public void Recreate(Extent2D swapExtent)
	{
		_swapExtent = swapExtent;
		foreach (var resource in _graph.Resources)
		{
			ref var slot = ref _images[resource.Id];
			if (slot.IsBackbuffer || !slot.SwapchainSized)
				continue;
			DestroyImage(ref slot);
			Allocate(ref slot, ExtentOf(resource));
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

	private void Allocate(ref ResourceImage slot, Extent2D extent)
	{
		slot.Extent = extent;

		ImageCreateInfo info = new()
		{
			SType = StructureType.ImageCreateInfo,
			ImageType = ImageType.Type2D,
			Format = slot.Format,
			Extent = new Extent3D(extent.Width, extent.Height, 1),
			MipLevels = 1,
			ArrayLayers = 1,
			Samples = SampleCountFlags.Count1Bit,
			Tiling = ImageTiling.Optimal,
			Usage = slot.Usage,
			SharingMode = SharingMode.Exclusive,
			InitialLayout = ImageLayout.Undefined,
		};
		_vk.CreateImage(_ctx.Device, in info, null, out slot.Image).Check("vkCreateImage");

		_vk.GetImageMemoryRequirements(_ctx.Device, slot.Image, out var requirements);
		MemoryAllocateInfo alloc = new()
		{
			SType = StructureType.MemoryAllocateInfo,
			AllocationSize = requirements.Size,
			MemoryTypeIndex = _ctx.FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
		};
		_vk.AllocateMemory(_ctx.Device, in alloc, null, out slot.Memory).Check("vkAllocateMemory");
		_vk.BindImageMemory(_ctx.Device, slot.Image, slot.Memory, 0).Check("vkBindImageMemory");

		ImageViewCreateInfo viewInfo = new()
		{
			SType = StructureType.ImageViewCreateInfo,
			Image = slot.Image,
			ViewType = ImageViewType.Type2D,
			Format = slot.Format,
			SubresourceRange = new ImageSubresourceRange(slot.Aspect, 0, 1, 0, 1),
		};
		_vk.CreateImageView(_ctx.Device, in viewInfo, null, out slot.View).Check("vkCreateImageView");
	}

	private void DestroyImage(ref ResourceImage slot)
	{
		if (slot.View.Handle != 0)
			_vk.DestroyImageView(_ctx.Device, slot.View, null);
		if (slot.Image.Handle != 0)
			_vk.DestroyImage(_ctx.Device, slot.Image, null);
		if (slot.Memory.Handle != 0)
			_vk.FreeMemory(_ctx.Device, slot.Memory, null);
		slot.View = default;
		slot.Image = default;
		slot.Memory = default;
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
		for (var i = 0; i < _images.Length; i++)
			if (!_images[i].IsBackbuffer)
				DestroyImage(ref _images[i]);
	}
}
