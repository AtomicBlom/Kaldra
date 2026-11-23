using Silk.NET.Vulkan;

namespace BinaryVibrance.Kaldra.Backend.Vulkan;

/// <summary>Thrown when a Vulkan call returns a non-success <see cref="Result"/>.</summary>
public sealed class VulkanException(string message) : Exception(message);
