using System.Runtime.CompilerServices;

using Loom.Core;
using Silk.NET.Core.Contexts;

namespace Loom.Vulkan;

/// <summary>
/// The Vulkan <see cref="IRenderBackend"/> — the factory that stands up a Vulkan device and returns a
/// <see cref="VulkanEngine"/> as a neutral <see cref="IEngine"/>. It self-registers into
/// <see cref="Engine.AvailableEngines"/> from a module initializer, so simply referencing (loading) this
/// assembly makes "Vulkan" a selectable backend — no explicit wiring in app code.
/// </summary>
public sealed class VulkanBackend : IRenderBackend
{
	/// <summary>Register the Vulkan backend as the Loom.Vulkan assembly loads (Planning.md §5 backend registry).</summary>
	[ModuleInitializer]
	internal static void Register() => Core.Engine.Register(new VulkanBackend());

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
