using System.Diagnostics;
using Silk.NET.Core;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Loom.Backend.Vulkan;

/// <summary>
/// Vulkan device bring-up: the Vulkan instance (+ optional validation / debug messenger), the
/// window surface, physical-device selection, and a logical device with the Vulkan 1.3 feature
/// set Loom's design assumes — dynamic rendering, synchronization2, and timeline semaphores.
/// Backend-only; never referenced from Core/Graph.
/// </summary>
public sealed unsafe class VulkanContext : IDisposable
{
	private static readonly string[] ValidationLayers = ["VK_LAYER_KHRONOS_validation"];
	private static readonly string[] RequiredDeviceExtensions = [KhrSwapchain.ExtensionName];

	private readonly bool _validation;
	private readonly bool _wantDebugUtils; // VK_EXT_debug_utils: messenger (validation) and/or object names + labels (UseNamedGPUObjects)
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

	/// <summary>Nanoseconds per timestamp tick (<c>VkPhysicalDeviceLimits.timestampPeriod</c>) — converts query
	/// ticks to wall time for the GPU profiler.</summary>
	public float TimestampPeriod { get; private set; }

	/// <summary>Valid bits in a timestamp written on the graphics queue (0 ⇒ the queue cannot timestamp, so GPU
	/// timing is unavailable). Used to mask query results and gate the profiler.</summary>
	public uint GraphicsTimestampValidBits { get; private set; }

	/// <summary>
	/// True if <c>VK_EXT_swapchain_colorspace</c> was enabled at instance creation — without it the surface
	/// only reports the SDR sRGB colour space, so HDR swapchain colour spaces are unavailable. The
	/// swapchain only attempts an HDR format when this is set.
	/// </summary>
	public bool HdrColorSpaceAvailable { get; private set; }

	private readonly bool _preferDiscreteGpu;

	public VulkanContext(IVkSurface surface, bool enableValidation = true, bool preferDiscreteGpu = true)
	{
		Vk = Vk.GetApi();
		_preferDiscreteGpu = preferDiscreteGpu;

		// Validation layers live in the Vulkan SDK, which may not be installed. Degrade gracefully.
		_validation = enableValidation && CheckValidationLayerSupport();
		if (enableValidation && !_validation)
			Console.Error.WriteLine(
				"[loom] Validation requested but VK_LAYER_KHRONOS_validation is unavailable " +
				"(install the Vulkan SDK to enable it). Continuing without validation.");

		// VK_EXT_debug_utils backs the validation messenger and, when the engine is built with UseNamedGPUObjects,
		// GPU object names + per-pass debug-label scopes for RenderDoc. Enable it if either wants it.
		_wantDebugUtils = _validation;
#if LOOM_NAMED_GPU_OBJECTS
		_wantDebugUtils = true;
#endif

		// Bring-up is not exception-safe on its own: a failure after CreateInstance (no 1.3 device, missing
		// surface/swapchain extension) would leak the live instance/surface/messenger because Dispose never runs.
		// Dispose is handle-guarded, so tearing down the partially-built context on failure is safe.
		try
		{
			CreateInstance(surface);
			if (_wantDebugUtils && Vk.TryGetInstanceExtension(Instance, out ExtDebugUtils debugUtils))
				_debugUtils = debugUtils;
			if (_validation)
				SetUpDebugMessenger();
			CreateSurface(surface);
			PickPhysicalDevice();
			CreateLogicalDevice();
		}
		catch
		{
			Dispose();
			throw;
		}
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

	private HashSet<string> AvailableInstanceExtensions()
	{
		uint count = 0;
		Vk.EnumerateInstanceExtensionProperties((byte*)null, ref count, null);
		var properties = new ExtensionProperties[count];
		var names = new HashSet<string>();
		fixed (ExtensionProperties* pProperties = properties)
		{
			Vk.EnumerateInstanceExtensionProperties((byte*)null, ref count, pProperties);
			for (uint i = 0; i < count; i++)
				names.Add(SilkMarshal.PtrToString((nint)pProperties[i].ExtensionName) ?? "");
		}
		return names;
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
		if (_wantDebugUtils)
			extensions = [.. extensions, ExtDebugUtils.ExtensionName];

		// VK_EXT_swapchain_colorspace lets the surface report HDR colour spaces (scRGB, HDR10) so the swapchain
		// can negotiate one when EngineSettings.HdrOutput is set. Opt-in only if the loader has it;
		// absence is fine — the swapchain falls back to SDR sRGB.
		const string swapchainColorspace = "VK_EXT_swapchain_colorspace";
		if (AvailableInstanceExtensions().Contains(swapchainColorspace))
		{
			extensions = [.. extensions, swapchainColorspace];
			HdrColorSpaceAvailable = true;
		}

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
		if (_debugUtils is null)
			return; // Validation requested but VK_EXT_debug_utils is absent — not fatal.

		DebugUtilsMessengerCreateInfoEXT createInfo = default;
		PopulateDebugCreateInfo(ref createInfo);
		_debugUtils.CreateDebugUtilsMessenger(Instance, in createInfo, null, out _debugMessenger)
			.Check("vkCreateDebugUtilsMessengerEXT");
	}

	// GPU object names + per-pass debug-label scopes (UseNamedGPUObjects), for RenderDoc/Nsight captures. The
	// helpers are [Conditional] so calls (and their argument evaluation, e.g. interpolated names) vanish entirely
	// when the engine is built without the flag; they no-op at runtime if VK_EXT_debug_utils wasn't enabled.

	/// <summary>Attach a human-readable name to a Vulkan object (image, buffer, pipeline, command buffer, …).</summary>
	[Conditional("LOOM_NAMED_GPU_OBJECTS")]
	public void SetObjectName(ObjectType type, ulong handle, string name)
	{
		if (_debugUtils is null)
			return;
		var pName = (byte*)SilkMarshal.StringToPtr(name);
		DebugUtilsObjectNameInfoEXT info = new()
		{
			SType = StructureType.DebugUtilsObjectNameInfoExt,
			ObjectType = type,
			ObjectHandle = handle,
			PObjectName = pName,
		};
		_debugUtils.SetDebugUtilsObjectName(Device, in info);
		SilkMarshal.Free((nint)pName);
	}

	/// <summary>Open a named debug-label region on a command buffer (a RenderDoc marker scope).</summary>
	[Conditional("LOOM_NAMED_GPU_OBJECTS")]
	public void BeginDebugLabel(CommandBuffer cmd, string label)
	{
		if (_debugUtils is null)
			return;
		var pLabel = (byte*)SilkMarshal.StringToPtr(label);
		DebugUtilsLabelEXT info = new() { SType = StructureType.DebugUtilsLabelExt, PLabelName = pLabel };
		_debugUtils.CmdBeginDebugUtilsLabel(cmd, in info);
		SilkMarshal.Free((nint)pLabel);
	}

	/// <summary>Close the most recently opened debug-label region on a command buffer.</summary>
	[Conditional("LOOM_NAMED_GPU_OBJECTS")]
	public void EndDebugLabel(CommandBuffer cmd) => _debugUtils?.CmdEndDebugUtilsLabel(cmd);

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
				TimestampPeriod = props.Limits.TimestampPeriod;
				chosenIsDiscrete = discrete;
				found = true;
			}

			// Stop early once the choice won't improve: a discrete GPU, or — when the host doesn't prefer discrete
			// (PreferDiscreteGpu=false, e.g. to match the integrated adapter for cross-backend diffing) — the first
			// suitable device of any type.
			if (found && (chosenIsDiscrete || !_preferDiscreteGpu))
				break;
		}

		if (!found)
			throw new VulkanException(
				"No Vulkan 1.3 device with graphics+present queues, swapchain, and the required features was found.");

		// The graphics family's timestamp support (GPU profiler): 0 valid bits ⇒ no timing on this queue.
		uint queueCount = 0;
		Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, ref queueCount, null);
		var queueFamilies = new QueueFamilyProperties[queueCount];
		fixed (QueueFamilyProperties* pQueueFamilies = queueFamilies)
			Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, ref queueCount, pQueueFamilies);
		GraphicsTimestampValidBits = GraphicsFamily < queueCount ? queueFamilies[GraphicsFamily].TimestampValidBits : 0;
	}

	private bool IsSuitable(PhysicalDevice device, out uint graphicsFamily, out uint presentFamily) =>
		FindQueueFamilies(device, out graphicsFamily, out presentFamily)
		&& SupportsRequiredExtensions(device)
		&& SupportsRequiredFeatures(device);

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

	private bool SupportsRequiredFeatures(PhysicalDevice device)
	{
		PhysicalDeviceVulkan11Features f11 = new() { SType = StructureType.PhysicalDeviceVulkan11Features };
		PhysicalDeviceVulkan12Features f12 = new()
		{
			SType = StructureType.PhysicalDeviceVulkan12Features,
			PNext = &f11,
		};
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
		return f13.DynamicRendering && f13.Synchronization2
			&& f12.TimelineSemaphore && f12.SamplerMirrorClampToEdge
			&& f11.ShaderDrawParameters;
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

		// Enable the features Loom assumes, chained off the device create-info pNext.
		// shaderDrawParameters (1.1) backs the SV_VertexID-based fullscreen triangle: Slang's
		// SV_VertexID lowering emits the SPIR-V DrawParameters capability.
		PhysicalDeviceVulkan11Features f11 = new()
		{
			SType = StructureType.PhysicalDeviceVulkan11Features,
			ShaderDrawParameters = true,
		};
		PhysicalDeviceVulkan12Features f12 = new()
		{
			SType = StructureType.PhysicalDeviceVulkan12Features,
			TimelineSemaphore = true,
			SamplerMirrorClampToEdge = true,
			PNext = &f11,
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

	/// <summary>
	/// Pick a memory type index satisfying <paramref name="typeBits"/> (a resource's allowed types) and the
	/// requested <paramref name="required"/> property flags. Shared by transient images and staging buffers.
	/// </summary>
	public uint FindMemoryType(uint typeBits, MemoryPropertyFlags required)
	{
		Vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out var properties);
		for (var i = 0u; i < properties.MemoryTypeCount; i++)
			if ((typeBits & (1u << (int)i)) != 0
				&& (properties.MemoryTypes[(int)i].PropertyFlags & required) == required)
				return i;
		throw new VulkanException($"No memory type with {required} available.");
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
