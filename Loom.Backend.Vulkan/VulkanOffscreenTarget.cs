using Silk.NET.Vulkan;

namespace Loom.Backend.Vulkan;

/// <summary>
/// An owned set of colour images the engine renders into with no window or swapchain — the present target for
/// <see cref="Loom.Core.Hosting.Surface.Offscreen"/> engines (CI screenshot regression, off-screen tooling,
/// software-rasterizer runs). Sized to the windowed swapchain's usual image count so the same per-frame-slot
/// bookkeeping applies; each image carries <see cref="ImageUsageFlags.ColorAttachmentBit"/> (rendered into) and
/// <see cref="ImageUsageFlags.TransferSrcBit"/> (so <c>RequestCapture</c> can copy it out), in the SDR
/// <see cref="Silk.NET.Vulkan.Format.B8G8R8A8Srgb"/> format — there is no display surface to negotiate an HDR
/// colour space against.
/// </summary>
internal sealed unsafe class VulkanOffscreenTarget : IVulkanPresentTarget
{
	private readonly VulkanContext _ctx;
	private readonly int _imageCount;
	private readonly List<DeviceMemory> _memories = [];

	public Format ImageFormat { get; } = Format.B8G8R8A8Srgb;
	public ColorSpaceKHR ColorSpace { get; } = ColorSpaceKHR.SpaceSrgbNonlinearKhr;
	public Extent2D Extent { get; private set; }
	public Image[] Images { get; private set; } = [];
	public ImageView[] ImageViews { get; private set; } = [];
	public uint ImageCount => (uint)Images.Length;
	public bool HdrActive => false;

	public VulkanOffscreenTarget(VulkanContext ctx, Extent2D extent, int imageCount)
	{
		_ctx = ctx;
		_imageCount = imageCount;
		Create(extent);
	}

	public void Recreate(Extent2D extent)
	{
		Destroy();
		Create(extent);
	}

	private void Create(Extent2D extent)
	{
		Extent = extent;
		Images = new Image[_imageCount];
		ImageViews = new ImageView[_imageCount];

		for (var i = 0; i < _imageCount; i++)
		{
			ImageCreateInfo imageInfo = new()
			{
				SType = StructureType.ImageCreateInfo,
				ImageType = ImageType.Type2D,
				Format = ImageFormat,
				Extent = new Extent3D(extent.Width, extent.Height, 1),
				MipLevels = 1,
				ArrayLayers = 1,
				Samples = SampleCountFlags.Count1Bit,
				Tiling = ImageTiling.Optimal,
				Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
				SharingMode = SharingMode.Exclusive,
				InitialLayout = ImageLayout.Undefined,
			};
			_ctx.Vk.CreateImage(_ctx.Device, in imageInfo, null, out var image).Check("vkCreateImage");

			_ctx.Vk.GetImageMemoryRequirements(_ctx.Device, image, out var requirements);
			MemoryAllocateInfo allocInfo = new()
			{
				SType = StructureType.MemoryAllocateInfo,
				AllocationSize = requirements.Size,
				MemoryTypeIndex = _ctx.FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
			};
			_ctx.Vk.AllocateMemory(_ctx.Device, in allocInfo, null, out var memory).Check("vkAllocateMemory");
			_ctx.Vk.BindImageMemory(_ctx.Device, image, memory, 0).Check("vkBindImageMemory");
			_memories.Add(memory);

			ImageViewCreateInfo viewInfo = new()
			{
				SType = StructureType.ImageViewCreateInfo,
				Image = image,
				ViewType = ImageViewType.Type2D,
				Format = ImageFormat,
				SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
			};
			_ctx.Vk.CreateImageView(_ctx.Device, in viewInfo, null, out var view).Check("vkCreateImageView");

			Images[i] = image;
			ImageViews[i] = view;
		}
	}

	private void Destroy()
	{
		foreach (var view in ImageViews)
			if (view.Handle != 0)
				_ctx.Vk.DestroyImageView(_ctx.Device, view, null);
		foreach (var image in Images)
			if (image.Handle != 0)
				_ctx.Vk.DestroyImage(_ctx.Device, image, null);
		foreach (var memory in _memories)
			if (memory.Handle != 0)
				_ctx.Vk.FreeMemory(_ctx.Device, memory, null);
		_memories.Clear();
		Images = [];
		ImageViews = [];
	}

	public void Dispose() => Destroy();
}
