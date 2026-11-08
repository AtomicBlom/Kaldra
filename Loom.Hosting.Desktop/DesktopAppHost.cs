using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

using Loom.Core.Diagnostics;
using Loom.Core.Hosting;

using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;

namespace Loom.Hosting.Desktop;

/// <summary>
/// The desktop <see cref="IAppHost"/>: creates a Silk.NET window for whichever backend shipped with the app,
/// brings up the engine, and drives the application's frame loop until the window closes. Backend-neutral — it
/// names no backend type; it reads whichever backends have already self-registered into
/// <see cref="Backends.Available"/> (the Loom SDK's generated bootstrap forces their module initializers to
/// run), falling back to a reflective <c>Loom.Backend.*.dll</c> scan under JIT only if none did, then picks the
/// first available and branches on its <see cref="IRenderBackend.Name"/> to build the right window surface.
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

		// Silk.NET discovers its windowing platform by reflection, which NativeAOT trims away — so an AOT-published
		// app throws "no window platform registered" at Window.Create. Register GLFW explicitly: required under AOT,
		// and a harmless no-op under JIT where the reflective discovery already ran. (Input's GlfwInput platform is
		// registered where the host first creates an input context.)
		GlfwWindowing.RegisterPlatform();

		var hostOptions = HostOptions.Parse(args);
		if (hostOptions.ShowHelp)
		{
			Console.WriteLine(HostOptions.Usage);
			return;
		}

		// Hand the app its own (non-host) flags before Configure runs, so an app can parse them in Configure.
		driver.SetArgs(hostOptions.AppArgs);

		var options = driver.ReadOptions();
		// Apply before ResolveBackend/engine creation so --backend/--vsync/--max-fps/--hdr/--watch-shaders take
		// effect; --backend overrides the app's own preference, the rest are the command line's to own outright.
		hostOptions.ApplyTo(options.EngineSettings);

		var backend = ResolveBackend(options.EngineSettings.PreferredBackend);
		// Record which backend actually got resolved (the app may have left PreferredBackend unset).
		options.EngineSettings.PreferredBackend = backend.Name;

		// A context-less window for DirectX12 (it binds its swapchain to the raw HWND); a Vulkan-surface window
		// otherwise. Both option sets are plain Silk types, so this needs no backend reference — only the name.
		// FramesPerSecond caps the render loop to EngineSettings.MaxFramesPerSecond (0 = unlimited) — the host, not
		// the engine, applies the cap.
		var size = new Vector2D<int>(options.Width, options.Height);
		var isDirectX12 = string.Equals(backend.Name, "DirectX12", StringComparison.OrdinalIgnoreCase);
		var fps = options.EngineSettings.MaxFramesPerSecond;
		// Under OnDemand the window is event-driven: it sleeps between events instead of running the loop at a fixed
		// interval, and RequestRender (WindowHostControl below) wakes it via ContinueEvents. FramesPerSecond still
		// caps the rate. Continuous keeps the default fixed-interval loop.
		var eventDriven = options.RenderMode == RenderMode.OnDemand;
		var windowOptions = isDirectX12
			? WindowOptions.Default with { Size = size, Title = options.Title, API = GraphicsAPI.None, FramesPerSecond = fps }
			: WindowOptions.DefaultVulkan with { Size = size, Title = options.Title, FramesPerSecond = fps };
		var window = Window.Create(windowOptions with { IsEventDriven = eventDriven });
		window.Initialize();

		object surface = isDirectX12
			? window.Native?.Win32?.Hwnd ?? throw new InvalidOperationException("The DirectX12 backend needs the window's Win32 HWND, which the platform did not expose.")
			: window.VkSurface ?? throw new InvalidOperationException("The Vulkan backend needs the window's surface, which the windowing platform did not expose.");

		using var engine = backend.Create(surface, window.FramebufferSize.X, window.FramebufferSize.Y, options.EngineSettings);

		// The input context comes first: the host control reaches the clipboard through its keyboard. It is closed as the
		// window closes (below), not at the end of this method: Silk's Run() resets the window as it returns, destroying
		// GLFW's window, and the context's disposal puts its callbacks back on that window, which read through the freed
		// handle crashed the process on exit (0xC0000005 in glfwSetMouseButtonCallback), on some exits and not others.
		var inputContext = window.CreateInput();
		IHostControl host = new WindowHostControl(window, inputContext);
		driver.Load(engine, host);

		// The host reports the initial size before the first frame, exactly like every later resize.
		driver.Resize(window.FramebufferSize.X, window.FramebufferSize.Y);

		// Pump the window's platform input devices into the app's neutral input queue. The pump takes the input
		// context (not the window), so a host with its own window can reuse it against its own context.
		var inputPump = new DesktopInputPump(inputContext, driver);
		var inputClosed = false;
		window.Closing += () =>
		{
			if (inputClosed)
				return;
			inputClosed = true;
			inputPump.Dispose();
			inputContext.Dispose();
		};

		// A 0x0 framebuffer (minimized) has nothing to render into — skip both the engine resize and driver.Resize
		// so SurfaceSize keeps the last non-zero size, and skip Tick entirely while it stays minimized.
		var minimized = false;
		window.FramebufferResize += fb =>
		{
			minimized = fb.X == 0 || fb.Y == 0;
			if (minimized)
				return;
			engine.Resize(fb.X, fb.Y);
			driver.Resize(fb.X, fb.Y);
		};
		window.FocusChanged += focused =>
		{
			// The OS drops the ups for anything held across a focus loss (alt-tab), so synthesize releases to keep
			// held keys/buttons from being stranded down.
			if (!focused && !inputClosed)
				inputPump.ReleaseAllHeld();
			driver.SetFocus(focused);
		};

		// The interactive frame loop: the window owns the pacing (capped by MaxFramesPerSecond above); each callback
		// advances the app one frame until it calls Exit() (RequestExit closes the window) or the window is closed.
		// Headless verification of a hosted app is done by in-process tests driving the engine offscreen, not by a
		// capture/smoke mode on this production host.
		// Where the desktop shows SDR white on the window's monitor, for an HDR frame's paper white, and the brightest it
		// shows, for its highlights: asked once a second, on the thread pool, so moving the window to another monitor or
		// moving Windows' "SDR content brightness" slider follows.
		var hwnd = window.Native?.Win32?.Hwnd ?? 0;
		var sinceWhite = double.MaxValue;
		Task<(float? White, float? Peak)>? luminance = null;
		var total = 0.0;
		window.Render += delta =>
		{
			if (minimized)
				return;
			total += delta;
			// Asked off the frame's thread: a DXGI factory and the display configuration take milliseconds, which on the
			// frame showed as a stall once a second. The answer is taken on the next frame after it arrives.
			if (luminance is { IsCompleted: true } asked)
			{
				var (nits, peak) = asked.Result;
				luminance = null;
				// A null answer goes through too: a monitor that can't say replaces one that could, rather than keeping its level.
				if (nits != engine.DisplaySdrWhiteNits)
				{
					engine.DisplaySdrWhiteNits = nits;
					LoomLog.Info(nits is { } white ? $"SDR white on the window's monitor: {white:F0} nits" : "the window's monitor reports no SDR white");
				}
				if (peak != engine.DisplayPeakNits)
				{
					engine.DisplayPeakNits = peak;
					LoomLog.Info(peak is { } brightest ? $"the window's monitor peaks at {brightest:F0} nits" : "the window's monitor reports no peak");
				}
			}
			if ((sinceWhite += delta) >= 1.0 && luminance == null)
			{
				sinceWhite = 0.0;
				luminance = Task.Run(() => (SdrWhiteLevel.Nits(hwnd), DisplayPeak.Nits(hwnd)));
			}
			inputPump.Update(delta);
			driver.Tick(new FrameTime(delta, total));
		};

		window.Run();

		driver.Unload();
		engine.WaitIdle();
	}

	// The Loom SDK generates a [ModuleInitializer] into the app (LoomBackendBootstrap.g.cs) that forces every
	// selected backend's module constructor via a compile-time typeof, so Backends.Available is normally
	// already populated by the time this runs — no scan needed. Only a non-SDK/hand-authored project (no generated
	// bootstrap) or a plugin DLL dropped beside the app falls through to the reflective scan below, and only under
	// JIT: Assembly.LoadFrom throws NotSupportedException on NativeAOT, so the scan is skipped entirely there
	// (IsDynamicCodeSupported == false) rather than risk that exception crashing startup.
	private static IRenderBackend ResolveBackend(string? preferredBackend)
	{
		var backends = Backends.Available;
		if (backends.Count == 0 && RuntimeFeature.IsDynamicCodeSupported)
			backends = ScanForBackends();

		if (backends.Count == 0)
			throw new InvalidOperationException(
				"No rendering backend is available. Ensure a Loom backend package (Loom.Backend.Vulkan / " +
				"Loom.Backend.DirectX12) ships with the app — the Loom SDK adds one for the target RID.");

		if (preferredBackend is not null)
			foreach (var candidate in backends)
				if (string.Equals(candidate.Name, preferredBackend, StringComparison.OrdinalIgnoreCase))
					return candidate;

		return backends[0];
	}

	// JIT-only fallback: load every Loom.Backend.*.dll beside the app and run its module initializer so the
	// backend self-registers. LoadFrom alone doesn't fire a module initializer (that's lazy, on first type
	// access), so RunModuleConstructor forces it — mirroring how the SDK-generated app bootstrap registers the
	// host. Broadened to swallow NotSupportedException too, in case a caller invokes this without the
	// IsDynamicCodeSupported guard.
	[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
		Justification = "Reached only under RuntimeFeature.IsDynamicCodeSupported (JIT). A NativeAOT or trimmed app " +
			"registers its backends through the SDK-generated LoomBackendBootstrap module initializer, which roots the " +
			"backend assemblies statically, so Backends.Available is already populated and this reflective scan is unreachable.")]
	private static IReadOnlyList<IRenderBackend> ScanForBackends()
	{
		foreach (var dll in Directory.EnumerateFiles(AppContext.BaseDirectory, "Loom.Backend.*.dll"))
		{
			try
			{
				var assembly = Assembly.LoadFrom(dll);
				RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
			}
			catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or TypeInitializationException or NotSupportedException)
			{
				// Not a loadable managed backend (or its initializer opted out) — skip; a real backend still registers.
			}
		}

		return Backends.Available;
	}

	// Wraps the Silk.NET window as the app's control channel back to the host — RequestExit closes the window,
	// which unwinds window.Run() and lets Run's teardown (Unload + WaitIdle) proceed.
	private sealed class WindowHostControl(IWindow window, IInputContext input) : IHostControl
	{
		public void RequestExit() => window.Close();

		// Wake the event-driven loop so the next frame renders (a no-op effect when not event-driven). Thread-safe
		// per Silk's ContinueEvents contract, so an app may invalidate from an off-thread callback.
		public void RequestRender() => window.ContinueEvents();

		// Silk reaches the system clipboard through a keyboard (GLFW's is the window's); with none attached there is no clipboard.
		public string? GetClipboardText() => input.Keyboards.Count > 0 ? input.Keyboards[0].ClipboardText : null;

		public void SetClipboardText(string text)
		{
			if (input.Keyboards.Count > 0)
				input.Keyboards[0].ClipboardText = text;
		}
	}
}
