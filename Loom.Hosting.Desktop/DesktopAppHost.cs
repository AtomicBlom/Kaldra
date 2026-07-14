using System.Reflection;
using System.Runtime.CompilerServices;

using Loom.Core.Hosting;

using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Loom.Hosting.Desktop;

/// <summary>
/// The desktop <see cref="IAppHost"/>: creates a Silk.NET window for whichever backend shipped with the app,
/// brings up the engine, and drives the application's frame loop until the window closes. Backend-neutral — it
/// names no backend type; it force-loads the <c>Loom.Backend.*</c> assemblies present next to the app (so their
/// module initializers register into <see cref="Engine.AvailableEngines"/>), then picks the first available and
/// branches on its <see cref="IRenderBackend.Name"/> to build the right window surface.
/// </summary>
public sealed class DesktopAppHost : IAppHost
{
#pragma warning disable CA2255 // ModuleInitializer is the intended self-registration hook, matching the backends.
	[ModuleInitializer]
	internal static void Register() => AppHost.Register(new DesktopAppHost());
#pragma warning restore CA2255

	public string Name => "Desktop";

	public void Run(LoomApplication app, string[] args)
	{
		ArgumentNullException.ThrowIfNull(app);
		var driver = (IHostedApplication)app;
		var options = driver.ReadOptions();

		var backend = ResolveBackend();

		// A context-less window for DirectX12 (it binds its swapchain to the raw HWND); a Vulkan-surface window
		// otherwise. Both option sets are plain Silk types, so this needs no backend reference — only the name.
		var size = new Vector2D<int>(options.Width, options.Height);
		var isDirectX12 = string.Equals(backend.Name, "DirectX12", StringComparison.OrdinalIgnoreCase);
		var window = Window.Create(isDirectX12
			? WindowOptions.Default with { Size = size, Title = options.Title, API = GraphicsAPI.None }
			: WindowOptions.DefaultVulkan with { Size = size, Title = options.Title });
		window.Initialize();

		object surface = isDirectX12
			? window.Native?.Win32?.Hwnd ?? throw new InvalidOperationException("The DirectX12 backend needs the window's Win32 HWND, which the platform did not expose.")
			: window.VkSurface ?? throw new InvalidOperationException("The Vulkan backend needs the window's surface, which the windowing platform did not expose.");

		using var engine = backend.Create(surface, window.FramebufferSize.X, window.FramebufferSize.Y,
			new EngineSettings { PreferredBackend = backend.Name });
		driver.Load(engine);

		window.FramebufferResize += fb => engine.Resize(fb.X, fb.Y);

		// A minimal built-in headless capture (--capture <path>): render a few frames so any delta-driven state
		// settles, grab one, then close. Not the full launch-option surface — that's a later unification.
		var capturePath = ArgValue(args, "--capture");
		var total = 0f;
		var frame = 0;
		window.Render += delta =>
		{
			total += (float)delta;
			driver.Tick(new FrameTime(delta, total));
			frame++;
			if (capturePath is { Length: > 0 })
			{
				if (frame == 3)
					engine.RequestCapture(capturePath);
				else if (frame == 4)
					window.Close();
			}
		};

		window.Run();
	}

	// Load every Loom.Backend.*.dll beside the app and run its module initializer so the backend self-registers,
	// then take the first available. The RID + Use* flags decided which backend DLLs shipped, so this loads
	// exactly those. LoadFrom alone doesn't fire a module initializer (that's lazy, on first type access), so
	// RunModuleConstructor forces it — mirroring how the SDK-generated app bootstrap registers the host.
	private static IRenderBackend ResolveBackend()
	{
		foreach (var dll in Directory.EnumerateFiles(AppContext.BaseDirectory, "Loom.Backend.*.dll"))
		{
			try
			{
				var assembly = Assembly.LoadFrom(dll);
				RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
			}
			catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or TypeInitializationException)
			{
				// Not a loadable managed backend (or its initializer opted out) — skip; a real backend still registers.
			}
		}

		var backends = Engine.AvailableEngines;
		return backends.Count > 0
			? backends[0]
			: throw new InvalidOperationException(
				"No rendering backend is available. Ensure a Loom backend package (Loom.Backend.Vulkan / " +
				"Loom.Backend.DirectX12) ships with the app — the Loom SDK adds one for the target RID.");
	}

	private static string? ArgValue(string[] args, string name)
	{
		for (var i = 0; i < args.Length - 1; i++)
			if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
				return args[i + 1];
		return null;
	}
}
