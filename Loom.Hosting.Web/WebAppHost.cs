using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

using Loom.Backend.WebGPU;
using Loom.Core.Hosting;

namespace Loom.Hosting.Web;

/// <summary>
/// The browser <see cref="IAppHost"/>: brings the WebGPU device up on a JS-supplied canvas, then drives the
/// application's frame loop through requestAnimationFrame. Unlike the desktop host, <see cref="Run"/> does not
/// block — a browser app can't block its entry thread — so it just registers the application; the JS boot helper
/// then calls the <see cref="StartApp"/> export with the page's canvas selector to start everything. The canvas
/// identity flows in from the page (an embedder owns their own <c>&lt;canvas&gt;</c>); it is never hardcoded here.
/// </summary>
public sealed partial class WebAppHost : IAppHost
{
#pragma warning disable CA2255 // ModuleInitializer is the intended self-registration hook, matching the backends.
	[ModuleInitializer]
	internal static void Register() => AppHost.Register(new WebAppHost());
#pragma warning restore CA2255

	public string Name => "Web";

	// Frame state, held statically because the JS side (StartApp and the rAF callback) drives it after the app's
	// Main has returned. The application itself is read from LoomApplication.Current — the JS start path can't
	// rely on this host's own module initializer having run (unreliable under WASM AOT).
	private static IEngine? _engine;
	private static string? _captureName;
	private static double _time;
	private static double _lastTimestampMs;
	private static int _frame;

	// Capture mode (?capture=<name>): a frozen 1/60 s clock and a one-shot grab at a fixed frame, so a headless
	// browser capture is deterministic and pixel-comparable with a desktop reference.
	private const double CaptureDelta = 1.0 / 60.0;
	private const int CaptureFrame = 10;

	public void Run(LoomApplication app, string[] args)
	{
		// Nothing to do: a browser app can't block, and the app instance is already published as
		// LoomApplication.Current by Run. The JS boot helper calls StartApp(canvas) to bring the device up.
	}

	/// <summary>
	/// The JS boot helper calls this with the page's canvas selector once the .NET runtime is up. Brings up the
	/// WebGPU device on that canvas, binds the engine to the current application, and starts the frame loop.
	/// </summary>
	[JSExport]
	[SupportedOSPlatform("browser")]
	internal static async Task StartApp(string canvasSelector)
	{
		if (LoomApplication.Current is null)
		{
			var noApp = "No Loom application was registered — did Program.Main call LoomApplication.Run?";
			SetStatus(noApp);
			ConsoleError(noApp);
			return;
		}
		try
		{
			await StartAppCore(canvasSelector);
		}
		catch (Exception ex)
		{
			var msg = $"ERROR: {ex}";
			SetStatus(msg);
			ConsoleError(msg);
			throw;
		}
	}

	[SupportedOSPlatform("browser")]
	private static async Task StartAppCore(string canvasSelector)
	{
		if (!IsWebGpuAvailable())
		{
			SetStatus("WebGPU is not available in this browser. Try a recent Chrome or Edge.");
			return;
		}

		var app = (IHostedApplication)LoomApplication.Current!;
		var options = app.ReadOptions();

		// Bring the device up (async) before creating the engine — every later WebGPU call is synchronous, so the
		// engine contract (a synchronous IRenderBackend.Create + Render) holds.
		var capsJson = await InitDevice(canvasSelector, WebGpuProtocol.Version);
		var surface = WebGpuSurface.FromCapsJson(canvasSelector, capsJson);

		var query = GetLocationSearch();
		_captureName = QueryValue(query, "capture");
		var captureViaPost = query.Contains("post", StringComparison.Ordinal);

		var (width, height) = ParseSize(GetCanvasSize(canvasSelector), options);
		var settings = new WebGpuEngineSettings
		{
			CaptureViaPost = captureViaPost,
			// Resolve the application's own embedded shaders (compiled to WGSL under "<AppAssembly>.Shaders."), which
			// the backend's default resolver — scoped to the backend's own shaders — can't see.
			ShaderResolver = ResolveEntryAssemblyShader,
		};
		// Copy the app's neutral settings (EnableValidation defaults off — see AppOptions.EngineSettings — VSync,
		// HdrOutput, MaxFramesPerSecond, WatchShaderDirectory) onto the WebGPU-specific instance, then restore the
		// backend name: CopyBaseSettingsTo would otherwise overwrite it with whatever (possibly null) the app set.
		options.EngineSettings.CopyBaseSettingsTo(settings);
		settings.PreferredBackend = "WebGPU";

		// Construct the one browser backend directly rather than through Engine.AvailableEngines: that registry is
		// populated by a module initializer, which is not reliably run under WASM AOT + trimming.
		var backend = new WebGpuBackend();
		var engine = backend.Create(surface, width, height, settings);
		_engine = engine;

		app.Load(engine, Host);
		// The host reports the initial size before the first frame, exactly like every later resize.
		app.Resize(width, height);
		// The web host does not observe page focus, so app.SetFocus is never called here: there is no JS import
		// wired to surface the page's visibility/focus state.

		SetStatus($"Loom running on {engine.DeviceName} ({width}x{height})");
		StartFrameLoop(Frame);
		StartResizeHandler(() => Resize(canvasSelector));
	}

	[SupportedOSPlatform("browser")]
	private static void Frame(double timestampMs)
	{
		var engine = _engine;
		if (LoomApplication.Current is not { } current || engine is null)
			return;
		var app = (IHostedApplication)current;

		double delta;
		if (_captureName is not null)
		{
			delta = CaptureDelta;
			_time = _frame * CaptureDelta;
		}
		else
		{
			delta = _lastTimestampMs > 0 ? (timestampMs - _lastTimestampMs) / 1000.0 : 1.0 / 60.0;
			_lastTimestampMs = timestampMs;
			_time += delta;
		}

		try
		{
			app.Tick(new FrameTime(delta, _time));
			if (_captureName is not null && _frame == CaptureFrame)
				engine.RequestCapture(_captureName); // late enough that any delta-driven state has settled
		}
		catch (Exception ex)
		{
			var msg = $"RENDER ERROR: {ex}";
			SetStatus(msg);
			ConsoleError(msg);
			_engine = null; // stop ticking after a fault
		}
		_frame++;
	}

	[SupportedOSPlatform("browser")]
	private static void Resize(string canvasSelector)
	{
		var engine = _engine;
		// Capture mode pins the render size to the frame the capture was requested at; ignore viewport resizes.
		if (engine is null || _captureName is not null || LoomApplication.Current is not { } current)
			return;
		var (width, height) = ParseSize(GetCanvasSize(canvasSelector), null);
		engine.Resize(width, height);
		((IHostedApplication)current).Resize(width, height);
	}

	// The app's control channel back to this host. The browser has no separate "window closed" event to hook, so
	// RequestExit itself performs the close teardown (Unload then WaitIdle) before stopping the frame loop.
	private static readonly IHostControl Host = new BrowserHostControl();

	private sealed class BrowserHostControl : IHostControl
	{
		public void RequestExit()
		{
			var engine = _engine;
			if (engine is null || LoomApplication.Current is not { } current)
				return;
			_engine = null; // stop ticking — Frame() no-ops once this is null
			((IHostedApplication)current).Unload();
			engine.WaitIdle();
		}
	}

	// Resolve a shader artifact (e.g. "triangle.wgsl" / "triangle.wgsl.json") from the application's own assembly,
	// whose shaders embed under "<AssemblyName>.Shaders." (the Loom shader-compile default).
	private static byte[]? ResolveEntryAssemblyShader(string nameWithExtension)
	{
		var assembly = Assembly.GetEntryAssembly();
		if (assembly is null)
			return null;
		var prefix = assembly.GetName().Name + ".Shaders.";
		using var stream = assembly.GetManifestResourceStream(prefix + nameWithExtension);
		if (stream is null)
			return null;
		var bytes = new byte[stream.Length];
		stream.ReadExactly(bytes);
		return bytes;
	}

	// Parse the "w,h" device-pixel size the JS side reports for the canvas, falling back to the app's options
	// (or 1280x720) when it can't be read.
	private static (int Width, int Height) ParseSize(string wh, AppOptions? options)
	{
		var parts = wh.Split(',');
		if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
			return (Math.Max(1, w), Math.Max(1, h));
		return (options?.Width ?? 1280, options?.Height ?? 720);
	}

	// Extract a query-string value (e.g. "?capture=triangle&post" -> QueryValue(…, "capture") == "triangle").
	private static string? QueryValue(string query, string key)
	{
		foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			var eq = pair.IndexOf('=');
			if (eq > 0 && pair[..eq] == key)
				return Uri.UnescapeDataString(pair[(eq + 1)..]);
		}
		return null;
	}

	// ── Host-loop [JSImport] surface into loom-webgpu.js (the device/canvas/DOM half the backend's own
	//    marshaling seam sits beside). ──
	private const string Module = "loom-webgpu";

	[JSImport("isWebGpuAvailable", Module)]
	[SupportedOSPlatform("browser")]
	private static partial bool IsWebGpuAvailable();

	[JSImport("initDevice", Module)]
	[SupportedOSPlatform("browser")]
	private static partial Task<string> InitDevice(string canvasSelector, int protocolVersion);

	[JSImport("getCanvasSize", Module)]
	[SupportedOSPlatform("browser")]
	private static partial string GetCanvasSize(string canvasSelector);

	[JSImport("getLocationSearch", Module)]
	[SupportedOSPlatform("browser")]
	private static partial string GetLocationSearch();

	[JSImport("setStatus", Module)]
	[SupportedOSPlatform("browser")]
	private static partial void SetStatus(string text);

	[JSImport("consoleError", Module)]
	[SupportedOSPlatform("browser")]
	private static partial void ConsoleError(string text);

	[JSImport("startFrameLoop", Module)]
	[SupportedOSPlatform("browser")]
	private static partial void StartFrameLoop([JSMarshalAs<JSType.Function<JSType.Number>>] Action<double> onFrame);

	[JSImport("startResizeHandler", Module)]
	[SupportedOSPlatform("browser")]
	private static partial void StartResizeHandler([JSMarshalAs<JSType.Function>] Action onResize);
}
