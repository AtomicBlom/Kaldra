using System.Diagnostics;
using System.Text;
using Loom.Core.Diagnostics;
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

	/// <summary>The selected physical device's properties — vendor/device ids, limits, and the pipeline-cache UUID a
	/// persistent cache is keyed and validated by.</summary>
	public PhysicalDeviceProperties PhysicalDeviceProperties
	{
		get
		{
			Vk.GetPhysicalDeviceProperties(PhysicalDevice, out var properties);
			return properties;
		}
	}

	/// <summary>Nanoseconds per timestamp tick (<c>VkPhysicalDeviceLimits.timestampPeriod</c>) — converts query
	/// ticks to wall time for the GPU profiler.</summary>
	public float TimestampPeriod { get; private set; }

	/// <summary>Valid bits in a timestamp written on the graphics queue (0 ⇒ the queue cannot timestamp, so GPU
	/// timing is unavailable). Used to mask query results and gate the profiler.</summary>
	public uint GraphicsTimestampValidBits { get; private set; }

	/// <summary>Sample counts the chosen device supports for colour <em>and</em> depth attachments both — the
	/// intersection of the two framebuffer limits, because a domain sizes both.</summary>
	public SampleCountFlags FramebufferSampleCounts { get; private set; }

	/// <summary>The device's <c>maxColorAttachments</c> limit — the most colour targets one pass may write.</summary>
	public uint MaxColorAttachments { get; private set; }

	/// <summary>The device's <c>minUniformBufferOffsetAlignment</c> — the stride a uniform ring's allocations must
	/// round up to for a descriptor offset or a dynamic offset to be legal.</summary>
	public ulong MinUniformBufferOffsetAlignment { get; private set; }

	/// <summary>Whether a 32-bit float image can be sampled with a linear filter on this device.</summary>
	public bool Float32Filterable { get; private set; }

	/// <summary>
	/// True if <c>VK_EXT_swapchain_colorspace</c> was enabled at instance creation — without it the surface
	/// only reports the SDR sRGB colour space, so HDR swapchain colour spaces are unavailable. The
	/// swapchain only attempts an HDR format when this is set.
	/// </summary>
	public bool HdrColorSpaceAvailable { get; private set; }

	private readonly bool _preferDiscreteGpu;

	/// <summary>
	/// Prefer a CPU (software) physical device — <see cref="Loom.Core.Hosting.EngineSettings.PreferSoftwareAdapter"/>
	/// — for GPU-less CI or deterministic software rendering. Vulkan software rasterizers (lavapipe, SwiftShader)
	/// aren't present on every box, so this degrades gracefully to the normal selection when none is found.
	/// </summary>
	private readonly bool _preferSoftwareAdapter;

	// True for a windowless device (Surface.Offscreen): no VK_KHR_surface/VK_KHR_swapchain, no present-queue
	// requirement — graphics queue only. See the offscreen constructor below.
	private readonly bool _offscreen;

	public VulkanContext(IVkSurface surface, bool enableValidation = true, bool preferDiscreteGpu = true, bool preferSoftwareAdapter = false)
		: this(surface, offscreen: false, enableValidation, preferDiscreteGpu, preferSoftwareAdapter)
	{
	}

	/// <summary>
	/// Bring up a surfaceless Vulkan device for offscreen rendering (<see cref="Loom.Core.Hosting.Surface.Offscreen"/>):
	/// no <c>VK_KHR_surface</c>/<c>VK_KHR_swapchain</c>, no present-queue requirement — graphics queue only. The
	/// engine renders into an owned <see cref="VulkanOffscreenTarget"/> instead of a swapchain.
	/// </summary>
	public VulkanContext(bool enableValidation = true, bool preferDiscreteGpu = true, bool preferSoftwareAdapter = false)
		: this(null, offscreen: true, enableValidation, preferDiscreteGpu, preferSoftwareAdapter)
	{
	}

	private VulkanContext(IVkSurface? surface, bool offscreen, bool enableValidation, bool preferDiscreteGpu, bool preferSoftwareAdapter)
	{
		Vk = Vk.GetApi();
		_preferDiscreteGpu = preferDiscreteGpu;
		_preferSoftwareAdapter = preferSoftwareAdapter;
		_offscreen = offscreen;

		// Validation layers live in the Vulkan SDK, which may not be installed. Degrade gracefully.
		_validation = enableValidation && CheckValidationLayerSupport();
		if (enableValidation && !_validation)
			LoomLog.Warning(
				"Validation requested but VK_LAYER_KHRONOS_validation is unavailable " +
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
			if (!_offscreen)
				CreateSurface(surface!);
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

	private void CreateInstance(IVkSurface? surface)
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

		// Surface extensions the windowing platform needs (e.g. VK_KHR_surface + the OS one) — none for an
		// offscreen (surfaceless) context.
		string[] extensions;
		if (_offscreen)
		{
			extensions = [];
		}
		else
		{
			byte** windowExtensions = surface!.GetRequiredExtensions(out uint windowExtCount);
			extensions = SilkMarshal.PtrToStringArray((nint)windowExtensions, (int)windowExtCount);
		}
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

		if (_offscreen)
			return; // no surface — VK_KHR_surface was never enabled above, so it can't be loaded here

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

	/// <summary>
	/// Open a named debug-label region on a command buffer (a RenderDoc marker scope). Unlike
	/// <see cref="SetObjectName"/>, which runs once per object at creation, this runs once per pass <em>per frame</em>
	/// — so the label is encoded into a stack buffer rather than marshalled through the heap. A capture annotation
	/// must not put the steady render loop in the GC's way.
	/// </summary>
	[Conditional("LOOM_NAMED_GPU_OBJECTS")]
	public void BeginDebugLabel(CommandBuffer cmd, string label)
	{
		if (_debugUtils is null)
			return;
		Span<byte> utf8 = stackalloc byte[MaxDebugLabelBytes];
		EncodeLabel(label, utf8);
		fixed (byte* pLabel = utf8)
		{
			DebugUtilsLabelEXT info = new() { SType = StructureType.DebugUtilsLabelExt, PLabelName = pLabel };
			_debugUtils.CmdBeginDebugUtilsLabel(cmd, in info);
		}
	}

	/// <summary>Stack buffer size for a per-frame debug label; longer names are clipped rather than allocated for.</summary>
	private const int MaxDebugLabelBytes = 256;

	/// <summary>
	/// UTF-8 encode <paramref name="label"/> into <paramref name="destination"/> with a trailing NUL, returning the
	/// byte count including it. A label too long for the buffer is clipped on a whole character (so the encoding stays
	/// valid) rather than growing the buffer: it is a capture annotation, and a clipped pass name in a capture beats an
	/// allocation on the frame path.
	/// </summary>
	private static int EncodeLabel(string label, Span<byte> destination)
	{
		var body = destination[..^1]; // reserve the NUL
		var chars = label.AsSpan();
		if (chars.Length > body.Length)
			chars = chars[..body.Length]; // one char is at least one UTF-8 byte, so this is the first useful bound
		int written;
		while (!Encoding.UTF8.TryGetBytes(chars, body, out written))
			chars = chars[..^1];
		destination[written] = 0;
		return written + 1;
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
		// The driver's own severity stays in the text: it is the validation layer's classification, not Loom's, and
		// collapsing the two would lose which one said what.
		var error = severity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt);
		var text = $"[vk:{(error ? "ERROR" : "WARN")}] {message}";
		if (error)
			LoomLog.Error(text);
		else
			LoomLog.Warning(text);
		return Vk.False;
	}

	private void CreateSurface(IVkSurface surface) =>
		Surface = surface.Create<AllocationCallbacks>(Instance.ToHandle(), null).ToSurface();

	private void PickPhysicalDevice()
	{
		var allDevices = Vk.GetPhysicalDevices(Instance).ToArray();
		var devices = allDevices;

		// A software (CPU) device — lavapipe, SwiftShader — isn't present on every box. Restrict the candidate
		// set to suitable CPU devices when one exists; otherwise fall through to the normal selection below
		// rather than throwing, so a request for software rendering degrades cleanly instead of failing engine
		// creation on a box with no software ICD installed.
		if (_preferSoftwareAdapter)
		{
			var software = allDevices.Where(IsSuitableSoftwareDevice).ToArray();
			if (software.Length > 0)
				devices = software;
			else
			{
				LoomLog.Warning(
					"--software-adapter requested but no Vulkan CPU (software) device (e.g. lavapipe, " +
					"SwiftShader) was found on this system; falling back to the normal best-available device.");
			}
		}

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
				// A domain sizes its colour and depth targets together, so the sample counts it may ask for are the
				// ones both support.
				FramebufferSampleCounts = props.Limits.FramebufferColorSampleCounts & props.Limits.FramebufferDepthSampleCounts;
				MaxColorAttachments = props.Limits.MaxColorAttachments;
				MinUniformBufferOffsetAlignment = props.Limits.MinUniformBufferOffsetAlignment;
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
				"No Vulkan 1.3 device with graphics+present queues, swapchain, and the required features was found."
				+ DescribeRejectedDevices(devices));

		// Loom binds every uniform buffer with a dynamic offset, so a pass's constant buffers are spent against
		// maxDescriptorSetUniformBuffersDynamic — whose guaranteed minimum is 8 — rather than against
		// maxDescriptorSetUniformBuffers, which guarantees 12. Loom.Core.Graph.GraphLimits.MaxUniformBuffersPerPass is exactly 8, so
		// the neutral cap now sits on the floor of the relevant limit with no margin at all. Name both numbers if a
		// device ever comes in under it, rather than letting descriptor-set creation fail somewhere further down.
		Vk.GetPhysicalDeviceProperties(PhysicalDevice, out var chosen);
		if (chosen.Limits.MaxDescriptorSetUniformBuffersDynamic < Loom.Core.Graph.GraphLimits.MaxUniformBuffersPerPass)
			throw new VulkanException(
				$"'{DeviceName}' supports only {chosen.Limits.MaxDescriptorSetUniformBuffersDynamic} dynamic uniform " +
				$"buffers per descriptor set, but a Loom pass may bind {Loom.Core.Graph.GraphLimits.MaxUniformBuffersPerPass} " +
				"(GraphLimits.MaxUniformBuffersPerPass). Vulkan guarantees 8, so this device is below spec.");

		// The graphics family's timestamp support (GPU profiler): 0 valid bits ⇒ no timing on this queue.
		uint queueCount = 0;
		Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, ref queueCount, null);
		var queueFamilies = new QueueFamilyProperties[queueCount];
		fixed (QueueFamilyProperties* pQueueFamilies = queueFamilies)
			Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, ref queueCount, pQueueFamilies);
		GraphicsTimestampValidBits = GraphicsFamily < queueCount ? queueFamilies[GraphicsFamily].TimestampValidBits : 0;

		// Linear filtering of a 32-bit float image is optional in Vulkan and absent on some tiled GPUs, which is why
		// R32F targets carry a "sample it nearest" caveat rather than being assumed filterable.
		Vk.GetPhysicalDeviceFormatProperties(PhysicalDevice, Format.R32G32B32A32Sfloat, out var float32Properties);
		Float32Filterable = float32Properties.OptimalTilingFeatures.HasFlag(FormatFeatureFlags.SampledImageFilterLinearBit);
	}

	private bool IsSuitable(PhysicalDevice device, out uint graphicsFamily, out uint presentFamily) =>
		FindQueueFamilies(device, out graphicsFamily, out presentFamily)
		&& SupportsRequiredExtensions(device)
		&& SupportsRequiredFeatures(device);

	/// <summary>A suitable device whose <c>PhysicalDeviceType</c> is <see cref="PhysicalDeviceType.Cpu"/> — a
	/// software rasterizer (lavapipe, SwiftShader) rather than a real GPU.</summary>
	private bool IsSuitableSoftwareDevice(PhysicalDevice device)
	{
		if (!IsSuitable(device, out _, out _))
			return false;
		Vk.GetPhysicalDeviceProperties(device, out var props);
		return props.DeviceType == PhysicalDeviceType.Cpu;
	}

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

			// Offscreen: no surface to test present support against — the graphics family alone is enough.
			if (_offscreen)
				presentFamily = graphicsFamily;
			else
			{
				KhrSurface.GetPhysicalDeviceSurfaceSupport(device, i, Surface, out var presentSupport);
				if (presentFamily == uint.MaxValue && presentSupport)
					presentFamily = i;
			}

			if (graphicsFamily != uint.MaxValue && presentFamily != uint.MaxValue)
				return true;
		}

		return false;
	}

	private bool SupportsRequiredExtensions(PhysicalDevice device)
	{
		if (_offscreen)
			return true; // no swapchain device extension needed without a surface to present to

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

	// Why each candidate was turned down, appended to the no-suitable-device throw. Built only on the failure path,
	// so the cost of naming things falls entirely on the run that is about to end anyway.
	private string DescribeRejectedDevices(PhysicalDevice[] devices)
	{
		if (devices.Length == 0)
			return " No Vulkan devices were reported at all — check that a driver is installed.";

		var lines = new StringBuilder();
		foreach (var device in devices)
		{
			Vk.GetPhysicalDeviceProperties(device, out var props);
			var name = SilkMarshal.PtrToString((nint)props.DeviceName) ?? "<unknown>";

			string reason;
			if (!FindQueueFamilies(device, out _, out _))
				reason = "no queue family supports both graphics and presentation";
			else if (!SupportsRequiredExtensions(device))
				reason = $"missing device extension(s): {string.Join(", ", RequiredDeviceExtensions)}";
			else
			{
				var missing = MissingRequiredFeatures(device);
				reason = missing.Count > 0 ? $"missing feature(s): {string.Join(", ", missing)}" : "unknown";
			}

			lines.Append(Environment.NewLine).Append("  ").Append(name).Append(" — ").Append(reason);
		}
		return Environment.NewLine + "Rejected:" + lines;
	}

	private bool SupportsRequiredFeatures(PhysicalDevice device) => MissingRequiredFeatures(device).Count == 0;

	/// <summary>
	/// The names of the required device features <paramref name="device"/> does not advertise, empty when it has all
	/// of them. Named rather than folded into one boolean so a box with no suitable device is told <em>which</em>
	/// feature is missing — the difference between "update your driver" and an afternoon of guessing.
	/// </summary>
	private List<string> MissingRequiredFeatures(PhysicalDevice device)
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

		var missing = new List<string>();
		if (!f13.DynamicRendering) missing.Add("dynamicRendering");
		if (!f13.Synchronization2) missing.Add("synchronization2");
		if (!f12.TimelineSemaphore) missing.Add("timelineSemaphore");
		if (!f12.SamplerMirrorClampToEdge) missing.Add("samplerMirrorClampToEdge");
		if (!f11.ShaderDrawParameters) missing.Add("shaderDrawParameters");
		return missing;
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

		// No VK_KHR_swapchain for an offscreen device — there is no surface to present to.
		string[] deviceExtensionNames = _offscreen ? [] : RequiredDeviceExtensions;
		byte** deviceExtensions = deviceExtensionNames.Length > 0 ? (byte**)SilkMarshal.StringArrayToPtr(deviceExtensionNames) : null;
		DeviceCreateInfo createInfo = new()
		{
			SType = StructureType.DeviceCreateInfo,
			QueueCreateInfoCount = (uint)uniqueFamilies.Length,
			PQueueCreateInfos = queueInfos,
			EnabledExtensionCount = (uint)deviceExtensionNames.Length,
			PpEnabledExtensionNames = deviceExtensions,
			PNext = &f13,
		};

		var result = Vk.CreateDevice(PhysicalDevice, in createInfo, null, out var device);
		if (deviceExtensions is not null)
			SilkMarshal.Free((nint)deviceExtensions);
		result.Check("vkCreateDevice");
		Device = device;

		Vk.GetDeviceQueue(Device, GraphicsFamily, 0, out var graphicsQueue);
		Vk.GetDeviceQueue(Device, PresentFamily, 0, out var presentQueue);
		GraphicsQueue = graphicsQueue;
		PresentQueue = presentQueue;

		if (_offscreen)
			return; // no swapchain — VK_KHR_swapchain was never enabled above, so it can't be loaded here

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
