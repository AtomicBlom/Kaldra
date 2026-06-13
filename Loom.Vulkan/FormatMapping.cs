using CoreFormat = Loom.Core.Format;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Loom.Vulkan;

/// <summary>
/// Translates the backend-neutral <see cref="CoreFormat"/> vocabulary (Planning.md §5) into concrete
/// Vulkan formats. This is the only place the abstract formats become Vulkan-specific — keeping it out
/// of Core/Graph is what lets a DX12 backend supply its own mapping later.
/// </summary>
internal static class FormatMapping
{
	/// <summary>Map an abstract resource format to its Vulkan format.</summary>
	public static VkFormat ToVulkan(CoreFormat format) => format switch
	{
		CoreFormat.Rgba8Srgb => VkFormat.R8G8B8A8Srgb,
		CoreFormat.Rgba8Unorm => VkFormat.R8G8B8A8Unorm,
		CoreFormat.Rgb10A2 => VkFormat.A2B10G10R10UnormPack32,
		CoreFormat.Rgba16F => VkFormat.R16G16B16A16Sfloat,
		CoreFormat.R16F => VkFormat.R16Sfloat,
		CoreFormat.R32Uint => VkFormat.R32Uint,
		CoreFormat.Rg16F => VkFormat.R16G16Sfloat,
		CoreFormat.Depth32F => VkFormat.D32Sfloat,
		_ => throw new VulkanException($"No Vulkan format mapping for {format}."),
	};
}
