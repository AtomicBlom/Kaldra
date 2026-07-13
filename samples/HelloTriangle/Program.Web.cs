using System.Reflection;
using System.Runtime.InteropServices.JavaScript;

using HelloTriangle;
using Loom.Backend.WebGPU;
using Loom.Core.Hosting;
using Loom.Core.Materials;

// The "loom-webgpu" module (its exports) is registered by main.js via setModuleImports before Main runs, so the
// backend's and this host's [JSImport]s resolve without an ImportAsync round-trip. Mirrors Loom.Web/Program.cs's
// boot sequence, trimmed to one technique, no demo gallery, no UI host, no pointer/resize wiring beyond capture.
try
{
	await BootAsync();
}
catch (Exception ex)
{
	Host.SetStatus($"ERROR: {ex}");
	Host.ConsoleError(ex.ToString());
	throw;
}

async Task BootAsync()
{
	if (!Host.IsWebGpuAvailable())
	{
		Host.SetStatus("WebGPU is not available in this browser. Try a recent Chrome or Edge.");
		return;
	}

	// Bring the device up (async) before creating the engine — every later WebGPU call is synchronous, so the
	// engine contract (a synchronous IRenderBackend.Create + Render) holds unchanged.
	var capsJson = await Host.InitDevice("#loom-canvas", WebGpuProtocol.Version);
	var surface = WebGpuSurface.FromCapsJson("#loom-canvas", capsJson);

	// Minimal headless verification hook, matching Loom.Web's ?capture=<name>&post protocol so the existing
	// Scripts/Verification/web-capture.ps1 works against this project unchanged.
	var query = Host.GetLocationSearch();
	var captureName = App.QueryValue(query, "capture");
	var captureViaPost = query.Contains("post", StringComparison.Ordinal);

	var (width, height) = App.ParseSize(Host.GetCanvasSize("#loom-canvas"));
	// The default resolver only checks the WebGPU backend's own embedded shaders, not this app's — this
	// project's one shader is embedded here (under "HelloTriangle.Shaders.", the AssemblyName default), so it
	// needs its own resolver, matching Loom.Web/Program.cs's ResolveEmbeddedShader exactly.
	var settings = new WebGpuEngineSettings { EnableValidation = true, CaptureViaPost = captureViaPost, ShaderResolver = ResolveEmbeddedShader };
	// Construct the backend directly rather than discovering it through Engine.AvailableEngines: that registry is
	// populated by a [ModuleInitializer], which is not reliably run under WASM AOT + trimming. Exactly one backend
	// exists here anyway (matches Loom.Web/Program.cs's own reasoning).
	var backend = new WebGpuBackend();
	var engine = backend.Create(surface, width, height, settings);

	engine.DefineTechnique("triangle", new TechniqueDescriptor("triangle"));
	engine.SetGraph(TriangleGraph.Build());

	App.Attach(engine, captureName);
	Host.SetStatus($"Loom running on {engine.DeviceName} ({width}x{height})");
	Host.StartFrameLoop(App.Frame);
	Host.StartResizeHandler(App.Resize);
}

// Resolve a shader artifact (e.g. "triangle.wgsl" / "triangle.wgsl.json") from this assembly's embedded
// resources — the default WebGpuEngineSettings.ShaderResolver only checks the backend's own embedded shaders.
static byte[]? ResolveEmbeddedShader(string nameWithExtension)
{
	var assembly = Assembly.GetExecutingAssembly();
	using var stream = assembly.GetManifestResourceStream($"HelloTriangle.Shaders.{nameWithExtension}");
	if (stream is null)
		return null;
	var bytes = new byte[stream.Length];
	stream.ReadExactly(bytes);
	return bytes;
}

/// <summary>The per-frame driver: the browser's requestAnimationFrame calls <see cref="Frame"/> via the JS shim.</summary>
internal static class App
{
	public static (int Width, int Height) ParseSize(string wh)
	{
		var parts = wh.Split(',');
		return parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)
			? (Math.Max(1, w), Math.Max(1, h))
			: (1280, 720);
	}

	/// <summary>Extract a query-string value (e.g. "?capture=triangle&amp;post" → QueryValue(…, "capture") == "triangle").</summary>
	public static string? QueryValue(string query, string key)
	{
		foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			var eq = pair.IndexOf('=');
			if (eq > 0 && pair[..eq] == key)
				return Uri.UnescapeDataString(pair[(eq + 1)..]);
		}
		return null;
	}

	private static IEngine? _engine;
	private static string? _captureName;
	private static float _time;
	private static double _lastTimestampMs;
	private static int _frame;

	// A frozen clock in capture mode, matching Loom.Web's deterministic-capture convention — irrelevant for a
	// static triangle's pixels, but keeps the frame-count-based capture trigger below reproducible regardless.
	private const double CaptureDelta = 1.0 / 60.0;
	private const int CaptureFrame = 10;

	public static void Attach(IEngine engine, string? captureName)
	{
		_engine = engine;
		_captureName = captureName;
	}

	public static void Resize()
	{
		// Capture mode pins the render size to the framebuffer the capture was requested at.
		if (_engine is null || _captureName is not null)
			return;
		var (width, height) = ParseSize(Host.GetCanvasSize("#loom-canvas"));
		_engine.Resize(width, height);
	}

	public static void Frame(double timestampMs)
	{
		if (_engine is null)
			return;

		double delta;
		if (_captureName is not null)
		{
			delta = CaptureDelta;
			_time = _frame * (float)CaptureDelta;
		}
		else
		{
			delta = _lastTimestampMs > 0 ? (timestampMs - _lastTimestampMs) / 1000.0 : 1.0 / 60.0;
			_lastTimestampMs = timestampMs;
			_time += (float)delta;
		}

		_engine.Render(new FrameInputs { DeltaSeconds = delta, Time = _time });

		if (_captureName is not null && _frame == CaptureFrame)
			_engine.RequestCapture(_captureName);

		_frame++;
	}
}

/// <summary>Host-side JS interop: device bring-up, status text, and the frame loop — mirrors
/// Loom.Web/Program.cs's Host partial class, trimmed to what this spike actually drives.</summary>
internal static partial class Host
{
	[JSImport("isWebGpuAvailable", "loom-webgpu")]
	public static partial bool IsWebGpuAvailable();

	[JSImport("initDevice", "loom-webgpu")]
	public static partial Task<string> InitDevice(string canvasSelector, int protocolVersion);

	[JSImport("setStatus", "loom-webgpu")]
	public static partial void SetStatus(string text);

	[JSImport("consoleError", "loom-webgpu")]
	public static partial void ConsoleError(string text);

	[JSImport("getCanvasSize", "loom-webgpu")]
	public static partial string GetCanvasSize(string canvasSelector);

	[JSImport("getLocationSearch", "loom-webgpu")]
	public static partial string GetLocationSearch();

	[JSImport("startFrameLoop", "loom-webgpu")]
	public static partial void StartFrameLoop([JSMarshalAs<JSType.Function<JSType.Number>>] Action<double> onFrame);

	[JSImport("startResizeHandler", "loom-webgpu")]
	public static partial void StartResizeHandler([JSMarshalAs<JSType.Function>] Action onResize);
}
