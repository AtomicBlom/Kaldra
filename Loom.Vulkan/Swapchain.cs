using Silk.NET.Vulkan;

namespace Loom.Vulkan;

/// <summary>
/// The swapchain plus its images and image views, sized to the window. Recreatable on resize
/// (e.g. <see cref="Result.ErrorOutOfDateKhr"/>). M0 plumbing — no graph involvement yet.
/// </summary>
public sealed unsafe class Swapchain : IDisposable
{
	private readonly VulkanContext _ctx;

	public SwapchainKHR Handle { get; private set; }
	public Format ImageFormat { get; private set; }
	public Extent2D Extent { get; private set; }
	public Image[] Images { get; private set; } = [];
	public ImageView[] ImageViews { get; private set; } = [];
	public uint ImageCount => (uint)Images.Length;

	public Swapchain(VulkanContext ctx, Extent2D windowExtent)
	{
		_ctx = ctx;
		Create(windowExtent);
	}

	public void Recreate(Extent2D windowExtent)
	{
		DestroyViews();
		var old = Handle;
		Create(windowExtent, old);
		if (old.Handle != 0)
			_ctx.KhrSwapchain.DestroySwapchain(_ctx.Device, old, null);
	}

	private void Create(Extent2D windowExtent, SwapchainKHR oldSwapchain = default)
	{
		_ctx.KhrSurface.GetPhysicalDeviceSurfaceCapabilities(_ctx.PhysicalDevice, _ctx.Surface, out var caps);

		var surfaceFormat = ChooseFormat();
		var presentMode = ChoosePresentMode();
		var extent = ChooseExtent(caps, windowExtent);

		var imageCount = caps.MinImageCount + 1;
		if (caps.MaxImageCount > 0 && imageCount > caps.MaxImageCount)
			imageCount = caps.MaxImageCount;

		// Color attachment always; add transfer-src when the surface supports it so frames
		// can be copied out (e.g. PNG capture for validation).
		var usage = ImageUsageFlags.ColorAttachmentBit;
		if ((caps.SupportedUsageFlags & ImageUsageFlags.TransferSrcBit) != 0)
			usage |= ImageUsageFlags.TransferSrcBit;

		SwapchainCreateInfoKHR createInfo = new()
		{
			SType = StructureType.SwapchainCreateInfoKhr,
			Surface = _ctx.Surface,
			MinImageCount = imageCount,
			ImageFormat = surfaceFormat.Format,
			ImageColorSpace = surfaceFormat.ColorSpace,
			ImageExtent = extent,
			ImageArrayLayers = 1,
			ImageUsage = usage,
			PreTransform = caps.CurrentTransform,
			CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
			PresentMode = presentMode,
			Clipped = true,
			OldSwapchain = oldSwapchain,
		};

		var indices = stackalloc uint[] { _ctx.GraphicsFamily, _ctx.PresentFamily };
		if (_ctx.GraphicsFamily != _ctx.PresentFamily)
		{
			createInfo.ImageSharingMode = SharingMode.Concurrent;
			createInfo.QueueFamilyIndexCount = 2;
			createInfo.PQueueFamilyIndices = indices;
		}
		else
		{
			createInfo.ImageSharingMode = SharingMode.Exclusive;
		}

		_ctx.KhrSwapchain.CreateSwapchain(_ctx.Device, in createInfo, null, out var swapchain)
			.Check("vkCreateSwapchainKHR");
		Handle = swapchain;
		ImageFormat = surfaceFormat.Format;
		Extent = extent;

		uint count = 0;
		_ctx.KhrSwapchain.GetSwapchainImages(_ctx.Device, Handle, ref count, null);
		Images = new Image[count];
		fixed (Image* pImages = Images)
			_ctx.KhrSwapchain.GetSwapchainImages(_ctx.Device, Handle, ref count, pImages);

		CreateViews();
	}

	private SurfaceFormatKHR ChooseFormat()
	{
		uint count = 0;
		_ctx.KhrSurface.GetPhysicalDeviceSurfaceFormats(_ctx.PhysicalDevice, _ctx.Surface, ref count, null);
		var formats = new SurfaceFormatKHR[count];
		fixed (SurfaceFormatKHR* pFormats = formats)
			_ctx.KhrSurface.GetPhysicalDeviceSurfaceFormats(_ctx.PhysicalDevice, _ctx.Surface, ref count, pFormats);

		foreach (var format in formats)
			if (format.Format == Format.B8G8R8A8Srgb && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
				return format;
		return formats[0];
	}

	private PresentModeKHR ChoosePresentMode()
	{
		uint count = 0;
		_ctx.KhrSurface.GetPhysicalDeviceSurfacePresentModes(_ctx.PhysicalDevice, _ctx.Surface, ref count, null);
		var modes = new PresentModeKHR[count];
		fixed (PresentModeKHR* pModes = modes)
			_ctx.KhrSurface.GetPhysicalDeviceSurfacePresentModes(_ctx.PhysicalDevice, _ctx.Surface, ref count, pModes);

		foreach (var mode in modes)
			if (mode == PresentModeKHR.MailboxKhr)
				return mode;
		return PresentModeKHR.FifoKhr; // Always available.
	}

	private static Extent2D ChooseExtent(SurfaceCapabilitiesKHR caps, Extent2D windowExtent)
	{
		if (caps.CurrentExtent.Width != uint.MaxValue)
			return caps.CurrentExtent;
		return new Extent2D(
			Math.Clamp(windowExtent.Width, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
			Math.Clamp(windowExtent.Height, caps.MinImageExtent.Height, caps.MaxImageExtent.Height));
	}

	private void CreateViews()
	{
		ImageViews = new ImageView[Images.Length];
		for (var i = 0; i < Images.Length; i++)
		{
			ImageViewCreateInfo info = new()
			{
				SType = StructureType.ImageViewCreateInfo,
				Image = Images[i],
				ViewType = ImageViewType.Type2D,
				Format = ImageFormat,
				SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
			};
			_ctx.Vk.CreateImageView(_ctx.Device, in info, null, out var view).Check("vkCreateImageView");
			ImageViews[i] = view;
		}
	}

	private void DestroyViews()
	{
		foreach (var view in ImageViews)
			if (view.Handle != 0)
				_ctx.Vk.DestroyImageView(_ctx.Device, view, null);
		ImageViews = [];
	}

	public void Dispose()
	{
		DestroyViews();
		if (Handle.Handle != 0)
			_ctx.KhrSwapchain.DestroySwapchain(_ctx.Device, Handle, null);
		Handle = default;
	}
}
