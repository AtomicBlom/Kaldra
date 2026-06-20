using System.Runtime.CompilerServices;

using Loom.Core;
using Loom.Core.Hosting;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;

namespace Loom.Vulkan;

/// <summary>
/// The Vulkan <see cref="IRenderBackend"/> — the factory that stands up a Vulkan device and returns a
/// <see cref="VulkanEngine"/> as a neutral <see cref="IEngine"/>. It self-registers into
/// <see cref="Engine.AvailableEngines"/> from a module initializer, so simply referencing (loading) this
/// assembly makes "Vulkan" a selectable backend — no explicit wiring in app code.
/// </summary>
public sealed class VulkanBackend : IRenderBackend
{
#pragma warning disable CA2255
	/// <summary>
	/// Register the Vulkan backend as the Loom.Vulkan assembly loads (Planning.md §5 backend registry) — but only
	/// if <see cref="IsSupported"/> confirms a Vulkan 1.3 device is actually present. An unusable backend then never
	/// appears in <see cref="Engine.AvailableEngines"/>, so the host's selection falls through to a working one
	/// instead of crashing later in <see cref="Create"/>.
	/// </summary>
	[ModuleInitializer]
	internal static void Register()
	{
		if (IsSupported())
			Engine.Register(new VulkanBackend());
	}
#pragma warning restore CA2255

	/// <summary>
	/// Probe — without throwing — whether this machine can run the Vulkan backend: the loader loads, a throwaway
	/// instance (no surface, no layers) comes up, and at least one physical device advertises the engine's Vulkan
	/// 1.3 baseline. The instance is destroyed before returning. Any failure (missing loader, no ICD, no 1.3-capable
	/// device, driver fault) is swallowed and reported as <c>false</c> rather than faulting assembly load.
	/// </summary>
	private static unsafe bool IsSupported()
	{
		try
		{
			var vk = Vk.GetApi();
			var appInfo = new ApplicationInfo
			{
				SType = StructureType.ApplicationInfo,
				ApiVersion = Vk.Version13,
			};
			var createInfo = new InstanceCreateInfo
			{
				SType = StructureType.InstanceCreateInfo,
				PApplicationInfo = &appInfo,
			};
			if (vk.CreateInstance(in createInfo, null, out var instance) != Result.Success)
				return false;

			try
			{
				uint count = 0;
				vk.EnumeratePhysicalDevices(instance, ref count, null);
				if (count == 0)
					return false;

				var devices = new PhysicalDevice[count];
				fixed (PhysicalDevice* pDevices = devices)
					vk.EnumeratePhysicalDevices(instance, ref count, pDevices);

				for (uint i = 0; i < count; i++)
				{
					vk.GetPhysicalDeviceProperties(devices[i], out var props);
					if (props.ApiVersion >= Vk.Version13) // a device meets the 1.3 baseline — the engine can come up
						return true;
				}
				return false;
			}
			finally
			{
				vk.DestroyInstance(instance, null);
			}
		}
		catch
		{
			return false; // no loader / no ICD / driver fault — Vulkan simply isn't available here
		}
	}

	public string Name => "Vulkan";

	/// <summary>
	/// Build a Vulkan device bound to <paramref name="surface"/> (the window's <see cref="IVkSurface"/>) and
	/// return a live engine. <paramref name="settings"/> may be a <see cref="VulkanEngineSettings"/> for
	/// Vulkan-only knobs (shader directory, GPU preference) or the base <see cref="EngineSettings"/>.
	/// </summary>
	public IEngine Create(object surface, int width, int height, EngineSettings settings)
	{
		ArgumentNullException.ThrowIfNull(surface);
		ArgumentNullException.ThrowIfNull(settings);
		if (surface is not IVkSurface vkSurface)
			throw new ArgumentException(
				$"The Vulkan backend needs the window's {nameof(IVkSurface)}; got {surface.GetType().Name}.",
				nameof(surface));

		var vulkanSettings = settings as VulkanEngineSettings ?? new VulkanEngineSettings
		{
			EnableValidation = settings.EnableValidation,
			VSync = settings.VSync,
			MaxFramesPerSecond = settings.MaxFramesPerSecond,
			HdrOutput = settings.HdrOutput,
			PreferredBackend = settings.PreferredBackend,
		};

		return new VulkanEngine(vkSurface, width, height, vulkanSettings);
	}
}
