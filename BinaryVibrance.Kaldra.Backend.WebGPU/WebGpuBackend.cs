using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

using BinaryVibrance.Kaldra.Core.Hosting;

namespace BinaryVibrance.Kaldra.Backend.WebGPU;

/// <summary>
/// The WebGPU <see cref="IRenderBackend"/> — browser-only. Unlike the native backends it cannot probe for
/// support at assembly load (device acquisition is an async browser API), so it registers unconditionally;
/// the host is responsible for checking <c>navigator.gpu</c> and awaiting the JS shim's <c>initDevice</c>
/// <em>before</em> calling <see cref="Create"/>, then passing the resulting <see cref="WebGpuSurface"/> here.
/// </summary>
public sealed class WebGpuBackend : IRenderBackend
{
#pragma warning disable CA2255
	[ModuleInitializer]
	internal static void Register() => Backends.Register(new WebGpuBackend());
#pragma warning restore CA2255

	public string Name => "WebGPU";

	[SupportedOSPlatform("browser")]
	public IEngine Create(object surface, int width, int height, EngineSettings settings)
	{
		ArgumentNullException.ThrowIfNull(surface);
		ArgumentNullException.ThrowIfNull(settings);
		if (surface is OffscreenSurface)
			throw new NotSupportedException("The WebGPU backend has no offscreen path; a canvas is always supplied by the page.");
		if (surface is not WebGpuSurface webGpuSurface)
			throw new ArgumentException(
				$"The WebGPU backend needs a {nameof(WebGpuSurface)} (built from the JS shim's initDevice caps); got {surface.GetType().Name}.",
				nameof(surface));

		var webGpuSettings = settings as WebGpuEngineSettings;
		if (webGpuSettings is null)
		{
			webGpuSettings = new WebGpuEngineSettings();
			settings.CopyBaseSettingsTo(webGpuSettings);
		}

		return new WebGpuEngine(webGpuSurface, width, height, webGpuSettings);
	}
}
