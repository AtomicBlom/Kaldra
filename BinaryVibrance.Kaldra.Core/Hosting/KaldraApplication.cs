using System.Numerics;

using BinaryVibrance.Kaldra.Core.Diagnostics;
using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Input;

namespace BinaryVibrance.Kaldra.Core.Hosting;

/// <summary>
/// The base class an application subclasses to run on Kaldra — a game, a CAD tool, a data-viz dashboard, an
/// editor: nothing here names a game concept, so it stays a neutral hosting abstraction beside <see cref="IEngine"/>
/// and <see cref="Engine"/>. Override the lifecycle hooks to author the app; hand an instance to
/// <see cref="Run"/> and the platform host (desktop window or browser canvas) owns the window/device bring-up
/// and the frame loop:
/// <code>
/// public sealed class MyApp : KaldraApplication
/// {
///     protected override void LoadContent()
///     {
///         Engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
///         SetGraph(BuildGraph());
///     }
///     protected override void Update(FrameTime time) { /* publish view-projection etc. */ }
/// }
/// // Program.cs (one line, shared by every platform):
/// KaldraApplication.Run(new MyApp(), args);
/// </code>
/// The per-frame submission (<see cref="IEngine.Render"/>) is framework-owned: the hooks only feed it. Under the
/// default <see cref="RenderMode.Continuous"/> it happens every frame; under <see cref="RenderMode.OnDemand"/> it
/// is gated on <see cref="Invalidate"/> / first-frame / resize.
/// </summary>
public abstract class KaldraApplication : IHostedApplication
{
	/// <summary>
	/// The application handed to the most recent <see cref="Run"/> call. The browser host reads it from its
	/// JS-driven start path (the runtime's module initializers are unreliable under WASM AOT, so the host can't
	/// rely on the registry to find the instance). Null before <see cref="Run"/> is called.
	/// </summary>
	public static KaldraApplication? Current { get; private set; }

	/// <summary>The live engine, valid from <see cref="LoadContent"/> onward (the host binds it after device bring-up).</summary>
	public IEngine Engine { get; private set; } = null!;

	/// <summary>
	/// The current framebuffer size in pixels, kept in sync by the host's resize notifications. Valid from
	/// <see cref="LoadContent"/> onward — the host reports the initial size before the first frame — so a
	/// projection can be computed as soon as content loads (<c>Camera.Perspective(fovY, SurfaceSize.X / SurfaceSize.Y, …)</c>)
	/// without needing a window/canvas reference of its own.
	/// </summary>
	public Vector2 SurfaceSize { get; private set; }

	/// <summary>
	/// The resolved input for the current frame — the bottom-of-stack (game-facing) held state after every input
	/// layer has had its say. Poll it during <see cref="Update"/>, <see cref="FixedUpdate"/>, or <see cref="BeforeRender"/>
	/// for level and edge queries (<c>Input.Down(Key.W)</c>, <c>Input.Pressed(Key.Space)</c>,
	/// <c>Input.PointerDelta</c>, <c>Input.Gamepad(0)</c>). Valid from construction — with no layers pushed it is
	/// simply the raw event stream folded into held state.
	/// </summary>
	/// <remarks>
	/// The stack resolves once per frame, at the top of the tick before the fixed-step catch-up loop, so this same
	/// snapshot is shared by <em>every</em> <see cref="FixedUpdate"/> step of the frame as well as <see cref="Update"/>
	/// and <see cref="BeforeRender"/>. A <see cref="InputSnapshot.Pressed(Key)"/> edge is therefore visible to all of a
	/// frame's fixed steps — guard once-per-press simulation logic accordingly rather than assuming one fixed step
	/// per edge.
	/// </remarks>
	protected InputSnapshot Input => _inputStack.Output;

	/// <summary>
	/// The command-line arguments the host did not consume as its own launch options — the app's own flags,
	/// verbatim and in order (empty on a host with no argument surface). Valid from <see cref="Configure"/> onward;
	/// parse it there for app-specific options (the reusable host owns backend/presentation flags itself).
	/// </summary>
	protected string[] AppArgs { get; private set; } = [];

	private IHostControl _host = null!;
	private double _fixedStepHz;
	private int _maxFixedSteps;
	private double _fixedAccumulator;
	private double _fixedTotalSeconds;

	// On-demand render gating (see AppOptions.RenderMode). Under OnDemand a frame is submitted only when the app
	// invalidated, on the first frame, or after a resize; otherwise the submit is skipped and the compositor holds
	// the last presented image. Under Continuous these stay irrelevant — ShouldRender always returns true.
	private RenderMode _renderMode;
	private bool _renderRequested;
	private bool _resizePending;
	private bool _firstFrameRendered;

	/// <summary>The per-frame input capacity shared by the queue, the fold, and the drain scratch.</summary>
	private const int InputEventCapacity = 1024;

	private readonly InputEventRing _inputQueue = new(InputEventCapacity);
	private readonly InputLayerStack _inputStack = new(InputEventCapacity);
	private readonly InputEvent[] _inputScratch = new InputEvent[InputEventCapacity];

	/// <summary>Set startup options (title, size). Runs once, before the window/canvas + engine exist.</summary>
	protected virtual void Configure(AppOptions options) { }

	/// <summary>Define techniques, upload meshes/textures/materials, and <see cref="SetGraph"/>. Runs once, after <see cref="Engine"/> is bound.</summary>
	protected virtual void LoadContent() { }

	/// <summary>Per-frame CPU work: publish view-projection / per-object data into the graph's app-writable buffers via <c>Engine.Write</c>. Runs before <see cref="BeforeRender"/> and the frame's render.</summary>
	protected virtual void Update(FrameTime time) { }

	/// <summary>
	/// Runs each frame immediately before the framework submits the active graph (<see cref="IEngine.Render"/>), with
	/// the frame's state fully resolved — the place for work that depends on everything else being settled: projecting
	/// a pointer hit onto the scene, compositing an in-world UI panel, writing debug-overlay lines.
	/// <para>
	/// Draw calls are not issued here. Kaldra is graph-based: a pass's closures are registered once (in the graph built
	/// in <see cref="LoadContent"/>) and the engine records them every frame whether or not this is overridden. To
	/// draw something, add a pass to the graph.
	/// </para>
	/// </summary>
	protected virtual void BeforeRender(FrameTime time) { }

	/// <summary>
	/// Runs at a constant rate (<c>1 / AppOptions.FixedStepHz</c>) independent of the display's frame rate — zero,
	/// one, or several times per frame depending on how the actual frame time lines up with the fixed step (a
	/// spiral-of-death guard caps the catch-up at <see cref="AppOptions.MaxFixedStepsPerFrame"/> steps per frame,
	/// dropping any further whole steps of backlog rather than falling ever further behind). Never called while
	/// <see cref="AppOptions.FixedStepHz"/> is 0 (the default). Put deterministic simulation/physics here rather
	/// than in <see cref="Update"/>; read <see cref="FrameTime.Alpha"/> in <see cref="Update"/>/<see cref="BeforeRender"/>
	/// to interpolate render state between the last two fixed steps.
	/// </summary>
	protected virtual void FixedUpdate(FixedTime time) { }

	/// <summary>
	/// Called when the framebuffer size changes: once with the initial size before the first frame, and again on
	/// every subsequent resize (the host skips this while minimized, so <see cref="SurfaceSize"/> holds the last
	/// non-zero size). <see cref="SurfaceSize"/> is already updated when this runs. Override to recompute a
	/// projection matrix or resize app-owned render targets; empty by default.
	/// </summary>
	protected virtual void OnResize(int width, int height) { }

	/// <summary>Called when the window/canvas gains or loses input focus. Override to pause gameplay, mute audio, or throttle background work; empty by default.</summary>
	protected virtual void OnFocusChanged(bool focused) { }

	/// <summary>
	/// Called when the app's frames stop because it has left the screen: minimized on the desktop, sent to the background
	/// or behind the lock screen on a phone. On a phone the process may be ended from here with no further call, not even
	/// <see cref="UnloadContent"/>, so save anything the player would miss now; pause the game clock and audio here too.
	/// No frame runs until <see cref="OnResumed"/>. Empty by default.
	/// </summary>
	protected virtual void OnSuspending() { }

	/// <summary>
	/// Called before the first frame after <see cref="OnSuspending"/>, as the app comes back on screen. The next frame's
	/// <see cref="FrameTime.DeltaSeconds"/> does not include the time away. Empty by default.
	/// </summary>
	protected virtual void OnResumed() { }

	/// <summary>Whether the app is suspended: between <see cref="OnSuspending"/> and <see cref="OnResumed"/>.</summary>
	protected bool IsSuspended { get; private set; }

	/// <summary>Release app-owned resources before the host tears down the engine. Runs once, on close, before <see cref="Engine"/> is disposed. Empty by default.</summary>
	protected virtual void UnloadContent() { }

	/// <summary>Make <paramref name="graph"/> the active graph (see <see cref="IEngine.SetGraph"/>). Call from <see cref="LoadContent"/>, or later to swap graphs at runtime.</summary>
	protected void SetGraph(RenderGraph graph) => Engine.SetGraph(graph);

	/// <summary>
	/// Ask the host to end the run — the desktop host closes the window; the web host stops its
	/// requestAnimationFrame loop. Safe to call from any lifecycle hook once <see cref="Engine"/> is bound.
	/// </summary>
	protected void Exit() => _host.RequestExit();

	/// <summary>The text on the system clipboard, or null when it holds none or the host has no clipboard.</summary>
	protected string? GetClipboardText() => _host.GetClipboardText();

	/// <summary>
	/// Put <paramref name="text"/> on the system clipboard. False when the host has no clipboard, so the app can say the
	/// copy did not happen (see <see cref="IHostControl.SetClipboardText"/>).
	/// </summary>
	protected bool SetClipboardText(string text) => _host.SetClipboardText(text);

	/// <summary>
	/// Offer the user a file to keep (see <see cref="IHostControl.SaveFile"/>): in the browser, its save dialog or a
	/// download. Call it from the input that asked for it. False when the host offers no files.
	/// </summary>
	protected bool SaveFile(string name, string contentType, ReadOnlySpan<byte> contents) => _host.SaveFile(name, contentType, contents);

	/// <summary>
	/// Whether <see cref="ShowFileDialogAsync"/> shows the system's file dialogs (see
	/// <see cref="IHostControl.CanShowFileDialogs"/>): the desktop host's do.
	/// </summary>
	protected bool CanShowFileDialogs => _host.CanShowFileDialogs;

	/// <summary>
	/// Show one of the system's file dialogs (see <see cref="IHostControl.ShowFileDialog"/>) while the frames go on. It
	/// completes on the main thread, between frames, with the paths chosen: none when the user cancelled, null when the
	/// host has no dialogs or the dialog couldn't be shown. Call it from the main thread.
	/// </summary>
	protected Task<IReadOnlyList<string>?> ShowFileDialogAsync(FileDialog dialog)
	{
		ArgumentNullException.ThrowIfNull(dialog);
		var chosen = new TaskCompletionSource<IReadOnlyList<string>?>();
		_host.ShowFileDialog(dialog, paths => chosen.TrySetResult(paths));
		return chosen.Task;
	}

	/// <summary>
	/// Files or folders were dropped on the window, by their full paths (see <see cref="IHostedApplication.DropFiles"/>).
	/// Called on the main thread, between frames; only the desktop host's window takes drops.
	/// </summary>
	protected virtual void OnFilesDropped(IReadOnlyList<string> paths) { }

	/// <summary>
	/// Whether the host can have the app fill the screen, over the browser's own bars (see
	/// <see cref="IHostControl.CanFullscreen"/>): only the browser host, so far.
	/// </summary>
	protected bool CanFullscreen => _host.CanFullscreen;

	/// <summary>
	/// Whether the app fills the screen, as far as the host knows: the player can leave fullscreen on their own (Esc, the
	/// Back gesture), so read it each time rather than remembering what was asked.
	/// </summary>
	protected bool IsFullscreen => _host.IsFullscreen;

	/// <summary>
	/// Ask the host to fill the screen, or stop (see <see cref="IHostControl.SetFullscreen"/>). Call it from the input that
	/// asked for it, which the browser requires. False when the host can't.
	/// </summary>
	protected bool SetFullscreen(bool on) => _host.SetFullscreen(on);

	/// <summary>
	/// Open a file of the app's packaged content by its path under the content root: the files the project lists as
	/// <c>KaldraContent</c>, which the Kaldra SDK packs where each host reads them (a <c>content</c> folder beside a desktop app,
	/// an Android APK's assets). The path is relative, with forward slashes, matching the file's case exactly, as
	/// <c>levels/1.json</c> for <c>Content\levels\1.json</c> under <c>&lt;KaldraContent Include="Content\**" /&gt;</c>.
	/// Throws <see cref="FileNotFoundException"/> for a file the app does not have.
	/// </summary>
	protected Stream OpenContent(string path) => _host.OpenContent(ContentPath(path));

	/// <summary>Read a whole file of the app's packaged content (see <see cref="OpenContent"/>).</summary>
	protected byte[] ReadContent(string path)
	{
		using var stream = OpenContent(path);
		using var bytes = new MemoryStream();
		stream.CopyTo(bytes);
		return bytes.ToArray();
	}

	/// <summary>
	/// Read a whole file of the app's packaged content (see <see cref="OpenContent"/>) off the calling thread, for loading
	/// while frames keep running.
	/// </summary>
	protected Task<byte[]> ReadContentAsync(string path)
	{
		var checkedPath = ContentPath(path); // a bad path fails at the call, not inside the task
		return Task.Run(() =>
		{
			using var stream = _host.OpenContent(checkedPath);
			using var bytes = new MemoryStream();
			stream.CopyTo(bytes);
			return bytes.ToArray();
		});
	}

	// A content path is relative and stays under the content root: forward slashes, no leading slash, no "..".
	private static string ContentPath(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		var normalized = path.Replace('\\', '/');
		if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || normalized.Split('/').Contains(".."))
			throw new ArgumentException($"A content path is relative to the content root and stays under it: '{path}'.", nameof(path));
		return normalized;
	}

	/// <summary>
	/// Physical pixels per layout unit on the display the app renders to (see <see cref="IHostControl.DisplayScale"/>):
	/// about 3 on a phone, 1 on a desktop monitor at 100%. Scale a UI's layout by it. Valid from
	/// <see cref="LoadContent"/> onward.
	/// </summary>
	protected float DisplayScale => _host.DisplayScale;

	/// <summary>
	/// How much of the bottom of the surface the host's on-screen keyboard covers, in surface pixels: zero while there is
	/// none, and always on a host whose keyboard is a physical one. A Vysara app hands it to its root each frame
	/// (<c>UIRoot.InputPaneHeight</c>), where elements marked <c>InputPane.AvoidsKeyboard</c> are arranged clear of it.
	/// </summary>
	protected float InputPaneHeight { get; private set; }

	/// <summary>
	/// How far in from each edge of the surface the screen hides content, in surface pixels: a phone's camera cutout, on the
	/// edge it is on for the current rotation. None on a desktop window. Keep text and controls inside it, while a background
	/// can still run to the edges. A Vysara app hands it to its root each frame (<c>UIRoot.SafeAreaInsets</c>), where
	/// elements marked <c>SafeArea.Edges</c> are arranged clear of it.
	/// </summary>
	protected ScreenInsets SafeAreaInsets { get; private set; }

	/// <summary>
	/// Request that the next frame be rendered. Only meaningful under <see cref="RenderMode.OnDemand"/>, where a
	/// frame is otherwise skipped: call this whenever app state changes so the change is drawn (an input handler, an
	/// async load completing, an animation advancing). Idempotent within a frame, and safe to call from any thread —
	/// it also wakes the desktop host's sleeping event loop. A no-op under <see cref="RenderMode.Continuous"/>.
	/// </summary>
	protected void Invalidate()
	{
		_renderRequested = true;
		_host?.RequestRender();
	}

	/// <summary>
	/// Pushes an input layer on top of the stack, where it is processed first each frame — it sees input before,
	/// and can block or inject it for, every layer beneath. The app assembles the stack bottom-up (gameplay first,
	/// then UI glue, then transient modals); whoever pushes a layer keeps the reference and removes it.
	/// </summary>
	protected void PushInputLayer(IInputLayer layer) => _inputStack.Push(layer);

	/// <summary>Removes and returns the top input layer, or null if the stack is empty.</summary>
	protected IInputLayer? PopInputLayer() => _inputStack.Pop();

	/// <summary>Removes a specific input layer from anywhere in the stack (not just the top); returns whether it was present.</summary>
	protected bool RemoveInputLayer(IInputLayer layer) => _inputStack.Remove(layer);

	/// <summary>Whether <paramref name="layer"/> is currently in the input stack.</summary>
	protected bool ContainsInputLayer(IInputLayer layer) => _inputStack.Contains(layer);

	void IHostedApplication.SetArgs(string[] appArgs) => AppArgs = appArgs;

	AppOptions IHostedApplication.ReadOptions()
	{
		var options = new AppOptions();
		Configure(options);
		_fixedStepHz = options.FixedStepHz;
		_maxFixedSteps = Math.Max(1, options.MaxFixedStepsPerFrame);
		_renderMode = options.RenderMode;
		return options;
	}

	void IHostedApplication.Load(IEngine engine, IHostControl host)
	{
		Engine = engine;
		_host = host;
		LoadContent();
	}

	void IHostedApplication.Resize(int width, int height)
	{
		SurfaceSize = new Vector2(width, height);
		_resizePending = true; // a resize recreates the swapchain, so the next frame must render even under OnDemand
		OnResize(width, height);
	}

	void IHostedApplication.SetFocus(bool focused) => OnFocusChanged(focused);

	void IHostedApplication.DropFiles(IReadOnlyList<string> paths) => OnFilesDropped(paths);

	void IHostedApplication.Suspend()
	{
		if (IsSuspended)
			return;
		IsSuspended = true;
		OnSuspending();
	}

	void IHostedApplication.Resume()
	{
		if (!IsSuspended)
			return;
		IsSuspended = false;
		OnResumed();
	}

	void IHostedApplication.QueueInput(in InputEvent e) => _inputQueue.Enqueue(in e);

	RumbleQueue IHostedApplication.Rumble => _inputStack.RumbleQueue;

	bool IHostedApplication.WantsTextInput => _inputStack.TextInputRequested;

	void IHostedApplication.SetInputPaneHeight(float pixels)
	{
		pixels = MathF.Max(0f, pixels);
		if (pixels == InputPaneHeight)
			return;
		InputPaneHeight = pixels;
		Invalidate(); // the layout changes, so an on-demand app draws again
	}

	void IHostedApplication.SetSafeArea(ScreenInsets insets)
	{
		if (insets == SafeAreaInsets)
			return;
		SafeAreaInsets = insets;
		Invalidate(); // the layout changes, so an on-demand app draws again
	}

	void IHostedApplication.Tick(FrameTime time)
	{
		// Resolve the layer stack once, at the very top of the frame before the fixed-step catch-up loop, so both
		// FixedUpdate and Update see this frame's input. The resulting Input snapshot is shared by every fixed step
		// of the frame (see the Input property's remarks on the edge double-fire caveat).
		var events = _inputScratch.AsSpan(0, _inputQueue.Drain(_inputScratch));
		_inputStack.Resolve(events);

		var frameTime = time;
		if (_fixedStepHz > 0)
		{
			var step = 1.0 / _fixedStepHz;
			_fixedAccumulator += time.DeltaSeconds;
			var steps = 0;
			while (_fixedAccumulator >= step && steps < _maxFixedSteps)
			{
				FixedUpdate(new FixedTime(step, _fixedTotalSeconds));
				_fixedAccumulator -= step;
				_fixedTotalSeconds += step;
				steps++;
			}
			// Spiral-of-death guard: drop the whole steps still owed rather than keep falling further behind, but keep
			// the fraction of a step, so Alpha goes on from where it was. Reset to 0, it jumped back to the start of a
			// step on every frame that dropped some, and fell short of the last one on every frame that didn't: what
			// was drawn between the fixed states moved by four steps one frame and six the next instead of five.
			if (_fixedAccumulator >= step)
				_fixedAccumulator %= step;
			frameTime = time with { Alpha = (float)(_fixedAccumulator / step) };
		}

		Update(frameTime);
		BeforeRender(frameTime);

		// Submit unconditionally under Continuous; under OnDemand only when the app invalidated, the first frame has
		// not yet been drawn, or a resize is pending. Update/Draw still run every tick (input routing, animation) —
		// only the whole-frame GPU submit is gated (present is atomic in the backend, so it is all-or-nothing).
		if (_renderMode == RenderMode.Continuous || !_firstFrameRendered || _resizePending || _renderRequested)
		{
			Engine.Render(new FrameInputs { DeltaSeconds = time.DeltaSeconds, Time = time.TotalSeconds });
			_renderRequested = false;
			_resizePending = false;
			_firstFrameRendered = true;
		}
	}

	void IHostedApplication.Unload() => UnloadContent();

	/// <summary>
	/// Run <paramref name="app"/> on the platform host. Reference a <c>BinaryVibrance.Kaldra.Hosting.*</c> package so a host is
	/// present — the RID picks it; the Kaldra SDK adds the right one and a module initializer registers it. On
	/// desktop this blocks until the window closes. On the browser it returns promptly: there is no host to
	/// dispatch to synchronously (the JS side supplies the canvas and starts the loop), so the app is published
	/// as <see cref="Current"/> for the browser host's JS-driven start path to pick up.
	/// </summary>
	public static void Run(KaldraApplication app, string[] args)
	{
		ArgumentNullException.ThrowIfNull(app);
		Current = app;
		var hosts = AppHost.Registered;
		if (hosts.Count > 0)
			hosts[0].Run(app, args);
		// No registered host: the browser path — the JS boot helper drives the app via Current after the runtime
		// is up. (A desktop build always has its host registered by the SDK-generated module initializer, so a
		// missing host there surfaces later as "no backend"/a blank run rather than here.)
	}
}
