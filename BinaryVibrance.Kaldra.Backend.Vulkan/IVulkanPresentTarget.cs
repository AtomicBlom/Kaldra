using Silk.NET.Vulkan;

namespace BinaryVibrance.Kaldra.Backend.Vulkan;

/// <summary>
/// The images the engine renders its present target into each frame, plus how they're resized/disposed — shared
/// by <see cref="VulkanSwapchain"/> (a live window's swapchain) and <see cref="VulkanOffscreenTarget"/> (owned
/// images with no window/surface, for headless/CI rendering via <see cref="BinaryVibrance.Kaldra.Core.Hosting.Surface.Offscreen"/>).
/// <see cref="VulkanEngine"/>'s per-frame recording loop is written once against this surface; only acquiring the
/// next image and handing the finished frame off differ between the two (kept out of this interface — the engine
/// branches those two calls directly on whichever concrete target it holds).
/// </summary>
internal interface IVulkanPresentTarget : IDisposable
{
	/// <summary>The present target's pixel format.</summary>
	Format ImageFormat { get; }

	/// <summary>The present target's colour space — always SDR sRGB for an offscreen target (no display to negotiate HDR against).</summary>
	ColorSpaceKHR ColorSpace { get; }

	/// <summary>The present target's pixel size.</summary>
	Extent2D Extent { get; }

	/// <summary>The backing images, one per frame slot.</summary>
	Image[] Images { get; }

	/// <summary>A view over each of <see cref="Images"/>.</summary>
	ImageView[] ImageViews { get; }

	/// <summary>The number of images (frame slots).</summary>
	uint ImageCount { get; }

	/// <summary>True when an HDR colour space was negotiated — always false for an offscreen target.</summary>
	bool HdrActive { get; }

	/// <summary>Rebuild the images at a new size (a resize).</summary>
	void Recreate(Extent2D extent);
}
