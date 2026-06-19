using Silk.NET.Vulkan;

namespace Loom.Vulkan;

internal static class VkCheck
{
	/// <summary>Throws a <see cref="VulkanException"/> unless the result is <see cref="Result.Success"/>.</summary>
	public static void Check(this Result result, string operation)
	{
		if (result != Result.Success)
			throw new VulkanException($"{operation} failed: {result}.");
	}
}
