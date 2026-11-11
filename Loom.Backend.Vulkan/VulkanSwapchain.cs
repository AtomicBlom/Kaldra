using Loom.Core.Diagnostics;
using Silk.NET.Vulkan;

namespace Loom.Backend.Vulkan;

/// <summary>
/// The swapchain plus its images and image views, sized to the window. Recreatable on resize
/// (e.g. <see cref="Result.ErrorOutOfDateKhr"/>).
/// </summary>
public sealed unsafe class VulkanSwapchain : IVulkanPresentTarget
{
	private readonly VulkanContext _ctx;
	private readonly bool _vsync;
	private readonly bool _hdrOutput;

	public SwapchainKHR Handle { get; private set; }
	public Format ImageFormat { get; private set; }
	public ColorSpaceKHR ColorSpace { get; private set; }
	public Extent2D Extent { get; private set; }
	public Image[] Images { get; private set; } = [];
	public ImageView[] ImageViews { get; private set; } = [];
	public uint ImageCount => (uint)Images.Length;

	/// <summary>
	/// True when an HDR colour space was negotiated (HDR output requested, the colour-space extension present,
	/// and the surface offered an HDR format). The terminal tone-map maps to display nits in this mode rather
	/// than compressing to SDR. False when HDR was not requested or the surface only offers SDR.
	/// </summary>
	public bool HdrActive { get; private set; }

	/// <summary>
	/// True when this swapchain's pre-transform differs from the surface's current transform, so the compositor rotates
	/// each frame and the presentation engine answers every present with <see cref="Result.SuboptimalKhr"/> (Android
	/// does). The present path treats that result as expected rather than rebuilding the swapchain each frame.
	/// </summary>
	public bool SuboptimalExpected { get; private set; }

	public VulkanSwapchain(VulkanContext ctx, Extent2D windowExtent, bool vsync, bool hdrOutput = false)
	{
		_ctx = ctx;
		_vsync = vsync;
		_hdrOutput = hdrOutput;
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
		HdrActive = surfaceFormat.ColorSpace != ColorSpaceKHR.SpaceSrgbNonlinearKhr;

		if (oldSwapchain.Handle == 0) // First create only — not every resize.
		{
			LoomLog.Info($"present={presentMode}, vsync={_vsync}");
			LoomLog.Info($"swapchain format={surfaceFormat.Format}, colorspace={surfaceFormat.ColorSpace}, hdr={HdrActive}");
			if (_hdrOutput && !HdrActive)
				LoomLog.Warning("HDR output requested but no HDR-capable surface format/colour space available — using SDR.");
		}

		// The engine renders upright and never rotates its output, so ask for IDENTITY and let the compositor rotate it into
		// the display's orientation. Matching caps.CurrentTransform instead would tell the presentation engine the image
		// is already rotated, and on a rotated Android device it would appear sideways. A surface without IDENTITY
		// (none known) falls back to its current transform.
		var preTransform = (caps.SupportedTransforms & SurfaceTransformFlagsKHR.IdentityBitKhr) != 0
			? SurfaceTransformFlagsKHR.IdentityBitKhr
			: caps.CurrentTransform;
		// Android reports VK_SUBOPTIMAL_KHR on every present whose preTransform differs from the current transform. That
		// is the cost of letting the compositor rotate, not a sign the swapchain is stale, so the present path must not
		// rebuild on it. Size changes still arrive as a resize or VK_ERROR_OUT_OF_DATE_KHR.
		SuboptimalExpected = preTransform != caps.CurrentTransform;

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
			PreTransform = preTransform,
			CompositeAlpha = ChooseCompositeAlpha(caps),
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
		ColorSpace = surfaceFormat.ColorSpace;
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

		// HDR output: negotiate scRGB (linear FP16). Its linear transfer means the terminal pass writes
		// its already-linear output straight through with no encode — the only HDR-specific change is the
		// tone-map preserving the extended range. Needs VK_EXT_swapchain_colorspace, or the surface never
		// reports it. (HDR10/PQ would need a PQ-encode in the terminal shader — deferred; scRGB covers Windows
		// HDR.) Falls through to the SDR sRGB choice when HDR isn't requested or isn't available.
		if (_hdrOutput && _ctx.HdrColorSpaceAvailable
			&& TryFind(formats, Format.R16G16B16A16Sfloat, ColorSpaceKHR.SpaceExtendedSrgbLinearExt, out var scrgb))
			return scrgb;

		// An sRGB format, so the hardware encodes the terminal pass's linear output: BGRA on desktop drivers, RGBA on
		// Android's, which seldom offer BGRA.
		if (TryFind(formats, Format.B8G8R8A8Srgb, ColorSpaceKHR.SpaceSrgbNonlinearKhr, out var srgb)
			|| TryFind(formats, Format.R8G8B8A8Srgb, ColorSpaceKHR.SpaceSrgbNonlinearKhr, out srgb))
			return srgb;
		LoomLog.Warning($"The surface offers no 8-bit sRGB format; presenting {formats[0].Format}, whose colours will not be sRGB-encoded.");
		return formats[0];
	}

	private static bool TryFind(SurfaceFormatKHR[] formats, Format format, ColorSpaceKHR colorSpace, out SurfaceFormatKHR found)
	{
		foreach (var candidate in formats)
			if (candidate.Format == format && candidate.ColorSpace == colorSpace)
			{
				found = candidate;
				return true;
			}
		found = default;
		return false;
	}

	private PresentModeKHR ChoosePresentMode()
	{
		uint count = 0;
		_ctx.KhrSurface.GetPhysicalDeviceSurfacePresentModes(_ctx.PhysicalDevice, _ctx.Surface, ref count, null);
		var modes = new PresentModeKHR[count];
		fixed (PresentModeKHR* pModes = modes)
			_ctx.KhrSurface.GetPhysicalDeviceSurfacePresentModes(_ctx.PhysicalDevice, _ctx.Surface, ref count, pModes);

		// Map the host's V-Sync *intent* to a present mode, picking the first available by preference.
		// vsync on  -> Fifo: honest no-tear vsync, guaranteed available, consistent on every GPU.
		// vsync off -> Immediate (uncapped, low-latency; the mode NVIDIA's Windows driver exposes),
		//              else Mailbox (uncapped where supported, e.g. Adreno), else Fifo as a last resort.
		ReadOnlySpan<PresentModeKHR> preference = _vsync
			? [PresentModeKHR.FifoKhr]
			: [PresentModeKHR.ImmediateKhr, PresentModeKHR.MailboxKhr, PresentModeKHR.FifoKhr];

		foreach (var wanted in preference)
			foreach (var available in modes)
				if (available == wanted)
					return wanted;
		return PresentModeKHR.FifoKhr; // Guaranteed available by the spec.
	}

	private static Extent2D ChooseExtent(SurfaceCapabilitiesKHR caps, Extent2D windowExtent)
	{
		if (caps.CurrentExtent.Width != uint.MaxValue)
			return caps.CurrentExtent;
		return new Extent2D(
			Math.Clamp(windowExtent.Width, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
			Math.Clamp(windowExtent.Height, caps.MinImageExtent.Height, caps.MaxImageExtent.Height));
	}

	// Opaque when the surface offers it (desktop drivers do). Many Android drivers offer only INHERIT, which leaves the
	// window's own opacity in charge; the engine's frames are opaque either way. Every surface supports at least one mode.
	private static CompositeAlphaFlagsKHR ChooseCompositeAlpha(SurfaceCapabilitiesKHR caps)
	{
		ReadOnlySpan<CompositeAlphaFlagsKHR> preference =
		[
			CompositeAlphaFlagsKHR.OpaqueBitKhr,
			CompositeAlphaFlagsKHR.InheritBitKhr,
			CompositeAlphaFlagsKHR.PreMultipliedBitKhr,
			CompositeAlphaFlagsKHR.PostMultipliedBitKhr,
		];
		foreach (var mode in preference)
			if ((caps.SupportedCompositeAlpha & mode) != 0)
				return mode;
		throw new VulkanException($"The surface supports no composite-alpha mode (SupportedCompositeAlpha={caps.SupportedCompositeAlpha}).");
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
