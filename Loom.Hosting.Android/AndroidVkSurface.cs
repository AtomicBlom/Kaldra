using System.Runtime.InteropServices;

using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;

namespace Loom.Hosting.Android;

/// <summary>
/// The Vulkan backend's view of an Android window: an <see cref="IVkSurface"/> over an <c>ANativeWindow</c>, so the
/// backend comes up on Android exactly as it does on a desktop window, naming no Android type. It asks for
/// <c>VK_KHR_surface</c> + <c>VK_KHR_android_surface</c> and makes the surface with
/// <c>vkCreateAndroidSurfaceKHR</c>, which Android's Vulkan loader exports.
/// </summary>
/// <remarks>
/// The host keeps its own reference on the window until the engine is disposed, so the window outlives every use the
/// backend can make of it.
/// </remarks>
internal sealed unsafe partial class AndroidVkSurface(nint nativeWindow) : IVkSurface
{
	// VK_STRUCTURE_TYPE_ANDROID_SURFACE_CREATE_INFO_KHR
	private const int AndroidSurfaceCreateInfoType = 1000008000;

	// The two instance extensions as one array of null-terminated UTF-8 names, made once: the backend reads them during
	// instance creation and keeps no pointer into them.
	private static readonly nint Extensions = BuildExtensionNames("VK_KHR_surface", "VK_KHR_android_surface");

	public VkNonDispatchableHandle Create<T>(VkHandle instance, T* allocator)
		where T : unmanaged
	{
		var info = new AndroidSurfaceCreateInfo { SType = AndroidSurfaceCreateInfoType, Window = nativeWindow };
		ulong surface;
		var result = vkCreateAndroidSurfaceKHR(instance.Handle, &info, allocator, &surface);
		if (result != 0)
			throw new InvalidOperationException($"vkCreateAndroidSurfaceKHR failed: VkResult {result}.");
		return new VkNonDispatchableHandle(surface);
	}

	public byte** GetRequiredExtensions(out uint count)
	{
		count = 2;
		return (byte**)Extensions;
	}

	private static nint BuildExtensionNames(params string[] names)
	{
		var array = (byte**)NativeMemory.Alloc((nuint)names.Length, (nuint)sizeof(byte*));
		for (var i = 0; i < names.Length; i++)
			array[i] = (byte*)Marshal.StringToCoTaskMemUTF8(names[i]);
		return (nint)array;
	}

	// VkAndroidSurfaceCreateInfoKHR: sType, pNext, flags, window. Sequential layout pads it as the C struct is padded.
	[StructLayout(LayoutKind.Sequential)]
	private struct AndroidSurfaceCreateInfo
	{
		public int SType;
		public void* PNext;
		public uint Flags;
		public nint Window;
	}

	[LibraryImport("vulkan")]
	private static partial int vkCreateAndroidSurfaceKHR(nint instance, AndroidSurfaceCreateInfo* createInfo, void* allocator, ulong* surface);
}
