using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

using Loom.Audio;
using Loom.Backend.WebGPU;
using Loom.Core.Hosting;

namespace Loom.Hosting.Web;

/// <summary>
/// The browser <see cref="IAppHost"/>: brings the WebGPU device up on a JS-supplied canvas, then runs a frame each
/// time the page calls <see cref="Frame"/>. Unlike the desktop host, <see cref="Run"/> does not block (a browser app
/// can't block its entry thread), so it just registers the application. The page then drives everything through two
/// exports: <see cref="StartApp"/> with its canvas selector, which brings the device up and starts queueing the page's
/// input, and <see cref="Frame"/> once a requestAnimationFrame, which runs one frame. StartApp starts no loop of its own,
/// so a page that never calls Frame gets no frames; loom-boot.js's runLoom does both, and its header lists the whole
/// contract for a bootstrap of an embedder's own. The canvas identity flows in from the page (an embedder owns their own
/// <c>&lt;canvas&gt;</c>); it is never hardcoded here.
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
	// The app's audio output, when it opened one: pumped once a frame, after the app's tick has queued the frame's sounds
	private static WebAudioDevice? _audio;
	private static string? _captureName;
	private static double _time;
	private static double _lastTimestampMs;
	private static int _frame;

	// The DOM/Gamepad-API → neutral input bridge, held for the lifetime of the app, fed the page's queued events at
	// the start of each frame
	private static WebInputPump? _input;
	private static string _canvasSelector = "";
	// The array the page's queued events are taken into each frame: 256 events of four numbers, more taken in turn when
	// more are queued
	private static readonly double[] _hostEvents = new double[1024];

	// The kind of each queued event, its first number: kept in lockstep with the HOST_* kinds in loom-webgpu.js
	private const int HostEventPointerMove = 1;
	private const int HostEventGamepadButton = 2;
	private const int HostEventGamepadAxis = 3;
	private const int HostEventInput = 4;
	private const int HostEventKey = 5;
	private const int HostEventResize = 6;
	private const int HostEventPaste = 7;
	private const int HostEventTouch = 8;
	private const int HostEventGamepadId = 9;

	// The text of a paste the page queued among this frame's events, which GetClipboardText answers with; null in a frame
	// with none
	private static string? _pastedText;

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
	/// The JS boot helper calls this with the page's canvas selector once the .NET runtime is up and the app's Main has run.
	/// Brings up the WebGPU device on that canvas, binds the engine to the current application, and starts queueing the
	/// page's input, resizes and pastes. It runs no frames: the page calls <see cref="Frame"/> once a requestAnimationFrame
	/// from then on.
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
		app.SetArgs([]); // the browser host has no command-line surface; apps read options from the page/query instead
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

		// Construct the one browser backend directly rather than through Backends.Available: that registry is
		// populated by a module initializer, which is not reliably run under WASM AOT + trimming.
		var backend = new WebGpuBackend();
		var engine = backend.Create(surface, width, height, settings);
		_engine = engine;

		app.Load(engine, Host);
		// The host reports the initial size and safe area before the first frame, exactly like every later resize.
		app.Resize(width, height);
		app.SetSafeArea(ParseInsets(GetSafeArea(canvasSelector)));

		// Bridge the page's DOM/Gamepad-API input into the app's neutral queue, and its focus/visibility state into
		// the app's focus hook. On blur/hide the pump synthesizes releases for everything held — the browser drops
		// the key/button ups for anything held across a tab switch, which would otherwise strand it down.
		_input = new WebInputPump(app.QueueInput, app.SetFocus, GetKeyCode);
		_canvasSelector = canvasSelector;
		StartHostEventQueue();
		StartClipboard();

		SetStatus($"Loom running on {engine.DeviceName} ({width}x{height})");
	}

	/// <summary>
	/// One frame, which the JS boot helper calls once a requestAnimationFrame: the input and resizes the page queued
	/// since the last, then the application's tick and render. Async to JS, as a multithreaded runtime requires of a
	/// call from the page's thread (its C# runs on a worker); the frame itself runs to the end before it returns.
	/// </summary>
	[JSExport]
	[SupportedOSPlatform("browser")]
	internal static Task Frame(double timestampMs)
	{
		var engine = _engine;
		if (LoomApplication.Current is not { } current || engine is null)
			return Task.CompletedTask;
		var app = (IHostedApplication)current;
		TakeHostEventsInto(_input!);

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
			// After the tick, so the sounds the frame started are in what is sent
			_audio?.Pump(delta);
			if (_captureName is not null && _frame == CaptureFrame)
				engine.RequestCapture(_captureName); // late enough that any delta-driven state has settled
		}
		catch (Exception ex)
		{
			var msg = $"RENDER ERROR: {ex}";
			SetStatus(msg);
			ConsoleError(msg);
			StopTicking(); // stop ticking after a fault
		}
		_frame++;
		return Task.CompletedTask;
	}

	// Frame no-ops from here on, and the page stops queueing input for frames that won't come to take it
	[SupportedOSPlatform("browser")]
	private static void StopTicking()
	{
		_engine = null;
		StopHostEventQueue();
	}

	// The page's queued events (startHostEventQueue in loom-webgpu.js), four numbers each, taken into one array held for
	// the app's lifetime, to the input pump, and a resize to the engine and the app. Nothing here allocates: a frame with a
	// stick held sends its axes every frame.
	[SupportedOSPlatform("browser")]
	private static void TakeHostEventsInto(WebInputPump input)
	{
		var events = _hostEvents;
		bool resized = false;
		_pastedText = null;
		int count;
		do
		{
			count = TakeHostEvents(events);
			for (int i = 0; i + 3 < count; i += 4)
			{
				double a = events[i + 1], b = events[i + 2], c = events[i + 3];
				switch ((int)events[i])
				{
					case HostEventPointerMove: input.OnPointerMove(a, b, c); break;
					case HostEventTouch: input.OnTouch(a, b, c); break;
					case HostEventGamepadButton: input.OnGamepadButton(a, b, c); break;
					case HostEventGamepadAxis: input.OnGamepadAxis(a, b, c); break;
					case HostEventInput: input.OnEvent(a, b, c); break;
					case HostEventKey: input.OnKey((int)a, b != 0); break;
					case HostEventGamepadId: input.OnGamepadId(a, GetGamepadId((int)a)); break;
					case HostEventResize: resized = true; break;
					case HostEventPaste: _pastedText = GetPastedText() is { Length: > 0 } text ? text : null; break;
				}
			}
		}
		while (count == events.Length);
		if (resized)
			Resize(_canvasSelector);
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
		var app = (IHostedApplication)current;
		app.Resize(width, height);
		// The safe area comes and goes with the window's size (a phone turned, fullscreen), and changes alone too (the
		// gesture bar), which the page reports as a resize. The app hears only a change.
		app.SetSafeArea(ParseInsets(GetSafeArea(canvasSelector)));
	}

	// The app's control channel back to this host. The browser has no separate "window closed" event to hook, so
	// RequestExit itself performs the close teardown (Unload then WaitIdle) before stopping the frame loop.
	private static readonly IHostControl Host = new BrowserHostControl();

	private sealed class BrowserHostControl : IHostControl
	{
		[SupportedOSPlatform("browser")]
		public void RequestExit()
		{
			var engine = _engine;
			if (engine is null || LoomApplication.Current is not { } current)
				return;
			StopTicking();
			((IHostedApplication)current).Unload();
			engine.WaitIdle();
		}

		// An AudioWorklet the frame loop sends the mix to (Frame pumps it after the app's tick)
		[SupportedOSPlatform("browser")]
		public IAudioDevice OpenAudioDevice(AudioOptions options)
		{
			var device = new WebAudioDevice(options);
			_audio = device;
			return device;
		}

		/// <summary>
		/// The text of a paste into the page in this frame, or null in a frame with none. Unlike the desktop host, this is not
		/// the clipboard as it stands: a browser asks the player before letting a page read the clipboard (and refuses outright
		/// in a cross-origin iframe), but a paste event carries its text without asking. Ctrl+V's paste comes straight after
		/// its keydown, so an app that reads the clipboard when it sees Ctrl+V gets the text pasted, in the same frame.
		/// </summary>
		public string? GetClipboardText() => _pastedText;

		// Taken, not done: the page writes it asynchronously, and the browser may still refuse it there.
		[SupportedOSPlatform("browser")]
		public bool SetClipboardText(string text)
		{
			WriteClipboardText(text);
			return true;
		}

		[SupportedOSPlatform("browser")]
		public bool SaveFile(string name, string contentType, ReadOnlySpan<byte> contents)
		{
			SaveFileJs(name, contentType, contents.ToArray());
			return true;
		}

		[SupportedOSPlatform("browser")]
		public bool CanFullscreen => CanFullscreenJs();

		[SupportedOSPlatform("browser")]
		public bool IsFullscreen => IsFullscreenJs();

		// Asked, not done: the page asks the browser, which may still refuse; a change arrives as a resize
		[SupportedOSPlatform("browser")]
		public bool SetFullscreen(bool on)
		{
			if (!CanFullscreenJs())
				return false;
			SetFullscreenJs(on);
			return true;
		}
	}

	// Resolve a shader artifact (e.g. "triangle.wgsl" / "triangle.wgsl.json") from the application's own assembly,
	// whose shaders embed under "<AssemblyName>.Shaders." (the Loom shader-compile default), or failing that from any
	// loaded library that embeds its own the same way (Loom.UI.SUIsei's "ui", with LoomEmbedWgsl). The app's come
	// first, so an app can stand in its own for a library's; two libraries embedding the same name is an error, since the
	// order assemblies are loaded in would otherwise pick one.
	private static byte[]? ResolveEntryAssemblyShader(string nameWithExtension)
	{
		var entry = Assembly.GetEntryAssembly();
		if (entry is not null && Read(entry) is { } own)
			return own;
		Assembly? found = null;
		foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (assembly == entry || assembly.IsDynamic || assembly.GetManifestResourceInfo(ResourceName(assembly)) is null)
				continue;
			if (found is not null)
				throw new InvalidOperationException(
					$"Shader '{nameWithExtension}' is embedded by both {found.GetName().Name} and {assembly.GetName().Name}; " +
					"rename one, or embed the one the app means in the app itself, which is looked in first.");
			found = assembly;
		}
		return found is null ? null : Read(found);

		string ResourceName(Assembly assembly) => assembly.GetName().Name + ".Shaders." + nameWithExtension;

		byte[]? Read(Assembly assembly)
		{
			using var stream = assembly.GetManifestResourceStream(ResourceName(assembly));
			if (stream is null)
				return null;
			var bytes = new byte[stream.Length];
			stream.ReadExactly(bytes);
			return bytes;
		}
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

	// Parse the "left,top,right,bottom" device-pixel insets the JS side reports for the canvas (getSafeArea), or none
	// when they can't be read.
	private static ScreenInsets ParseInsets(string ltrb)
	{
		var parts = ltrb.Split(',');
		if (parts.Length != 4)
			return ScreenInsets.None;
		Span<float> v = stackalloc float[4];
		for (var i = 0; i < 4; i++)
		{
			if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
				return ScreenInsets.None;
		}
		return new ScreenInsets(v[0], v[1], v[2], v[3]);
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

	// How far in from each of the canvas's edges the screen hides it, "left,top,right,bottom" in device pixels
	[JSImport("getSafeArea", Module)]
	[SupportedOSPlatform("browser")]
	private static partial string GetSafeArea(string canvasSelector);

	[JSImport("getLocationSearch", Module)]
	[SupportedOSPlatform("browser")]
	private static partial string GetLocationSearch();

	[JSImport("setStatus", Module)]
	[SupportedOSPlatform("browser")]
	private static partial void SetStatus(string text);

	[JSImport("consoleError", Module)]
	[SupportedOSPlatform("browser")]
	private static partial void ConsoleError(string text);

	// Queue the page's DOM/Gamepad-API input and resizes for TakeHostEvents, rather than calling into C# as they come
	[JSImport("startHostEventQueue", Module)]
	[SupportedOSPlatform("browser")]
	private static partial void StartHostEventQueue();

	// Fills destination with as many whole queued events as fit and returns how many numbers it wrote
	[JSImport("takeHostEvents", Module)]
	[SupportedOSPlatform("browser")]
	private static partial int TakeHostEvents([JSMarshalAs<JSType.MemoryView>] Span<double> destination);

	// The KeyboardEvent.code of a key id the page sent, asked once per id
	[JSImport("getKeyCode", Module)]
	[SupportedOSPlatform("browser")]
	private static partial string GetKeyCode(int id);

	// A pad's Gamepad.id as it last connected, asked as its id event is taken (just before it connects)
	[JSImport("getGamepadId", Module)]
	[SupportedOSPlatform("browser")]
	private static partial string GetGamepadId(int pad);

	// Drops what is queued and queues nothing more: called once the host stops taking events, or the queue would grow for
	// as long as the page stays open
	[JSImport("stopHostEventQueue", Module)]
	[SupportedOSPlatform("browser")]
	private static partial void StopHostEventQueue();

	[JSImport("startClipboard", Module)]
	[SupportedOSPlatform("browser")]
	private static partial void StartClipboard();

	[JSImport("getPastedText", Module)]
	[SupportedOSPlatform("browser")]
	private static partial string GetPastedText();

	[JSImport("writeClipboardText", Module)]
	[SupportedOSPlatform("browser")]
	private static partial void WriteClipboardText(string text);

	[JSImport("canFullscreen", Module)]
	[SupportedOSPlatform("browser")]
	private static partial bool CanFullscreenJs();

	[JSImport("isFullscreen", Module)]
	[SupportedOSPlatform("browser")]
	private static partial bool IsFullscreenJs();

	// Async on the JS side (the browser answers later); nothing waits on it
	[JSImport("setFullscreen", Module)]
	[SupportedOSPlatform("browser")]
	private static partial void SetFullscreenJs(bool on);

	// Async on the JS side (the save dialog); nothing waits on it
	[JSImport("saveFile", Module)]
	[SupportedOSPlatform("browser")]
	private static partial void SaveFileJs(string name, string contentType, byte[] bytes);
}
