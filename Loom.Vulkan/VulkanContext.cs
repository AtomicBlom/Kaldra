using Silk.NET.Core;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Loom.Vulkan;

/// <summary>
/// M0 device bring-up: the Vulkan instance (+ optional validation / debug messenger), the
/// window surface, physical-device selection, and a logical device with the Vulkan 1.3 feature
/// set Loom's design assumes — dynamic rendering, synchronization2, and timeline semaphores
/// (Planning.md §3, §8). Backend-only; never referenced from Core/Graph.
/// </summary>
public sealed unsafe class VulkanContext : IDisposable
{
	private static readonly string[] ValidationLayers = ["VK_LAYER_KHRONOS_validation"];
	private static readonly string[] RequiredDeviceExtensions = [KhrSwapchain.ExtensionName];

	private readonly bool _validation;
	private ExtDebugUtils? _debugUtils;
	private DebugUtilsMessengerEXT _debugMessenger;
	private PfnDebugUtilsMessengerCallbackEXT _debugCallback;

	public Vk Vk { get; }
	public Instance Instance { get; private set; }
	public SurfaceKHR Surface { get; private set; }
	public KhrSurface KhrSurface { get; private set; } = null!;
	public PhysicalDevice PhysicalDevice { get; private set; }
	public Device Device { get; private set; }
	public KhrSwapchain KhrSwapchain { get; private set; } = null!;
	public Queue GraphicsQueue { get; private set; }
	public Queue PresentQueue { get; private set; }
	public uint GraphicsFamily { get; private set; }
	public uint PresentFamily { get; private set; }
	public string DeviceName { get; private set; } = "<unknown>";

	public VulkanContext(IVkSurface surface, bool enableValidation = true)
	{
		Vk = Vk.GetApi();

		// Validation layers live in the Vulkan SDK, which may not be installed. Degrade gracefully.
		_validation = enableValidation && CheckValidationLayerSupport();
		if (enableValidation && !_validation)
			Console.Error.WriteLine(
				"[loom] Validation requested but VK_LAYER_KHRONOS_validation is unavailable " +
				"(install the Vulkan SDK to enable it). Continuing without validation.");

		CreateInstance(surface);
		if (_validation)
			SetUpDebugMessenger();
		CreateSurface(surface);
		PickPhysicalDevice();
		CreateLogicalDevice();
	}

	private bool CheckValidationLayerSupport()
	{
		uint count = 0;
		Vk.EnumerateInstanceLayerProperties(ref count, null);
		var layers = new LayerProperties[count];
		var available = new HashSet<string>();
		fixed (LayerProperties* pLayers = layers)
		{
			Vk.EnumerateInstanceLayerProperties(ref count, pLayers);
			for (uint i = 0; i < count; i++)
				available.Add(SilkMarshal.PtrToString((nint)pLayers[i].LayerName) ?? "");
		}

		foreach (var layer in ValidationLayers)
			if (!available.Contains(layer))
				return false;
		return true;
	}

	private void CreateInstance(IVkSurface surface)
	{
		ApplicationInfo appInfo = new()
		{
			SType = StructureType.ApplicationInfo,
			PApplicationName = (byte*)SilkMarshal.StringToPtr("Loom Sandbox"),
			ApplicationVersion = new Version32(1, 0, 0),
			PEngineName = (byte*)SilkMarshal.StringToPtr("Loom"),
			EngineVersion = new Version32(1, 0, 0),
			ApiVersion = Vk.Version13,
		};

		// Surface extensions the windowing platform needs (e.g. VK_KHR_surface + the OS one).
		byte** windowExtensions = surface.GetRequiredExtensions(out uint windowExtCount);
		var extensions = SilkMarshal.PtrToStringArray((nint)windowExtensions, (int)windowExtCount);
		if (_validation)
			extensions = [.. extensions, ExtDebugUtils.ExtensionName];

		InstanceCreateInfo createInfo = new()
		{
			SType = StructureType.InstanceCreateInfo,
			PApplicationInfo = &appInfo,
			EnabledExtensionCount = (uint)extensions.Length,
			PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions),
		};

		// Chain a debug-messenger create-info so instance create/destroy is also covered.
		DebugUtilsMessengerCreateInfoEXT debugCreateInfo = default;
		if (_validation)
		{
			createInfo.EnabledLayerCount = (uint)ValidationLayers.Length;
			createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(ValidationLayers);
			PopulateDebugCreateInfo(ref debugCreateInfo);
			createInfo.PNext = &debugCreateInfo;
		}

		var result = Vk.CreateInstance(in createInfo, null, out var instance);

		SilkMarshal.Free((nint)appInfo.PApplicationName);
		SilkMarshal.Free((nint)appInfo.PEngineName);
		SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
		if (_validation)
			SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);

		result.Check("vkCreateInstance");
		Instance = instance;

		if (!Vk.TryGetInstanceExtension(Instance, out KhrSurface khrSurface))
			throw new VulkanException("VK_KHR_surface instance extension is not available.");
		KhrSurface = khrSurface;
	}

	private void SetUpDebugMessenger()
	{
		if (!Vk.TryGetInstanceExtension(Instance, out ExtDebugUtils debugUtils))
			return; // Validation requested but VK_EXT_debug_utils is absent — not fatal.

		_debugUtils = debugUtils;
		DebugUtilsMessengerCreateInfoEXT createInfo = default;
		PopulateDebugCreateInfo(ref createInfo);
		_debugUtils.CreateDebugUtilsMessenger(Instance, in createInfo, null, out _debugMessenger)
			.Check("vkCreateDebugUtilsMessengerEXT");
	}

	private void PopulateDebugCreateInfo(ref DebugUtilsMessengerCreateInfoEXT createInfo)
	{
		_debugCallback = new PfnDebugUtilsMessengerCallbackEXT(DebugCallback);
		createInfo.SType = StructureType.DebugUtilsMessengerCreateInfoExt;
		createInfo.MessageSeverity =
			DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
			DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt;
		createInfo.MessageType =
			DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
			DebugUtilsMessageTypeFlagsEXT.ValidationBitExt |
			DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt;
		createInfo.PfnUserCallback = _debugCallback;
	}

	private static uint DebugCallback(
		DebugUtilsMessageSeverityFlagsEXT severity,
		DebugUtilsMessageTypeFlagsEXT messageTypes,
		DebugUtilsMessengerCallbackDataEXT* pCallbackData,
		void* pUserData)
	{
		var message = SilkMarshal.PtrToString((nint)pCallbackData->PMessage) ?? "";
		var level = severity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) ? "ERROR" : "WARN";
		Console.Error.WriteLine($"[vk:{level}] {message}");
		return Vk.False;
	}

	private void CreateSurface(IVkSurface surface) =>
		Surface = surface.Create<AllocationCallbacks>(Instance.ToHandle(), null).ToSurface();

	private void PickPhysicalDevice()
	{
		var devices = Vk.GetPhysicalDevices(Instance);
		var found = false;
		var chosenIsDiscrete = false;

		foreach (var device in devices)
		{
			if (!IsSuitable(device, out uint graphics, out uint present))
				continue;

			Vk.GetPhysicalDeviceProperties(device, out var props);
			var discrete = props.DeviceType == PhysicalDeviceType.DiscreteGpu;

			if (!found || (discrete && !chosenIsDiscrete))
			{
				PhysicalDevice = device;
				GraphicsFamily = graphics;
				PresentFamily = present;
				DeviceName = SilkMarshal.PtrToString((nint)props.DeviceName) ?? "<unknown>";
				chosenIsDiscrete = discrete;
				found = true;
				if (discrete)
					break; // Prefer the first discrete GPU.
			}
		}

		if (!found)
			throw new VulkanException(
				"No Vulkan 1.3 device with graphics+present queues, swapchain, and the required features was found.");
	}

	private bool IsSuitable(PhysicalDevice device, out uint graphicsFamily, out uint presentFamily) =>
		FindQueueFamilies(device, out graphicsFamily, out presentFamily)
		&& SupportsRequiredExtensions(device)
		&& Supports13Features(device);

	private bool FindQueueFamilies(PhysicalDevice device, out uint graphicsFamily, out uint presentFamily)
	{
		graphicsFamily = uint.MaxValue;
		presentFamily = uint.MaxValue;

		uint count = 0;
		Vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, null);
		var families = new QueueFamilyProperties[count];
		fixed (QueueFamilyProperties* pFamilies = families)
			Vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, pFamilies);

		for (uint i = 0; i < count; i++)
		{
			if (graphicsFamily == uint.MaxValue && families[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
				graphicsFamily = i;

			KhrSurface.GetPhysicalDeviceSurfaceSupport(device, i, Surface, out var presentSupport);
			if (presentFamily == uint.MaxValue && presentSupport)
				presentFamily = i;

			if (graphicsFamily != uint.MaxValue && presentFamily != uint.MaxValue)
				return true;
		}

		return false;
	}

	private bool SupportsRequiredExtensions(PhysicalDevice device)
	{
		uint count = 0;
		Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, null);
		var available = new ExtensionProperties[count];
		var names = new HashSet<string>();
		fixed (ExtensionProperties* pAvailable = available)
		{
			Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, pAvailable);
			for (uint i = 0; i < count; i++)
				names.Add(SilkMarshal.PtrToString((nint)pAvailable[i].ExtensionName) ?? "");
		}

		foreach (var required in RequiredDeviceExtensions)
			if (!names.Contains(required))
				return false;
		return true;
	}

	private bool Supports13Features(PhysicalDevice device)
	{
		PhysicalDeviceVulkan12Features f12 = new() { SType = StructureType.PhysicalDeviceVulkan12Features };
		PhysicalDeviceVulkan13Features f13 = new()
		{
			SType = StructureType.PhysicalDeviceVulkan13Features,
			PNext = &f12,
		};
		PhysicalDeviceFeatures2 features = new()
		{
			SType = StructureType.PhysicalDeviceFeatures2,
			PNext = &f13,
		};

		Vk.GetPhysicalDeviceFeatures2(device, &features);
		return f13.DynamicRendering && f13.Synchronization2 && f12.TimelineSemaphore;
	}

	private void CreateLogicalDevice()
	{
		uint[] uniqueFamilies = GraphicsFamily == PresentFamily
			? [GraphicsFamily]
			: [GraphicsFamily, PresentFamily];

		var priority = 1.0f;
		var queueInfos = stackalloc DeviceQueueCreateInfo[uniqueFamilies.Length];
		for (var i = 0; i < uniqueFamilies.Length; i++)
		{
			queueInfos[i] = new DeviceQueueCreateInfo
			{
				SType = StructureType.DeviceQueueCreateInfo,
				QueueFamilyIndex = uniqueFamilies[i],
				QueueCount = 1,
				PQueuePriorities = &priority,
			};
		}

		// Enable the 1.3 features Loom assumes, chained off the device create-info pNext.
		PhysicalDeviceVulkan12Features f12 = new()
		{
			SType = StructureType.PhysicalDeviceVulkan12Features,
			TimelineSemaphore = true,
		};
		PhysicalDeviceVulkan13Features f13 = new()
		{
			SType = StructureType.PhysicalDeviceVulkan13Features,
			DynamicRendering = true,
			Synchronization2 = true,
			PNext = &f12,
		};

		byte** deviceExtensions = (byte**)SilkMarshal.StringArrayToPtr(RequiredDeviceExtensions);
		DeviceCreateInfo createInfo = new()
		{
			SType = StructureType.DeviceCreateInfo,
			QueueCreateInfoCount = (uint)uniqueFamilies.Length,
			PQueueCreateInfos = queueInfos,
			EnabledExtensionCount = (uint)RequiredDeviceExtensions.Length,
			PpEnabledExtensionNames = deviceExtensions,
			PNext = &f13,
		};

		var result = Vk.CreateDevice(PhysicalDevice, in createInfo, null, out var device);
		SilkMarshal.Free((nint)deviceExtensions);
		result.Check("vkCreateDevice");
		Device = device;

		Vk.GetDeviceQueue(Device, GraphicsFamily, 0, out var graphicsQueue);
		Vk.GetDeviceQueue(Device, PresentFamily, 0, out var presentQueue);
		GraphicsQueue = graphicsQueue;
		PresentQueue = presentQueue;

		if (!Vk.TryGetDeviceExtension(Instance, Device, out KhrSwapchain khrSwapchain))
			throw new VulkanException("VK_KHR_swapchain device extension is not available.");
		KhrSwapchain = khrSwapchain;
	}

	public void Dispose()
	{
		if (Device.Handle != 0)
			Vk.DestroyDevice(Device, null);
		if (_debugUtils is not null && _debugMessenger.Handle != 0)
			_debugUtils.DestroyDebugUtilsMessenger(Instance, _debugMessenger, null);
		if (KhrSurface is not null && Surface.Handle != 0)
			KhrSurface.DestroySurface(Instance, Surface, null);
		if (Instance.Handle != 0)
			Vk.DestroyInstance(Instance, null);

		KhrSwapchain?.Dispose();
		KhrSurface?.Dispose();
		_debugUtils?.Dispose();
		Vk.Dispose();
	}
}
