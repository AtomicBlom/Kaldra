using System.Numerics;

using Loom.Core.Graph;
using Loom.Input;

namespace Loom.Core.Hosting;

/// <summary>
/// The base class an application subclasses to run on Loom — a game, a CAD tool, a data-viz dashboard, an
/// editor: nothing here names a game concept, so it stays a neutral hosting abstraction beside <see cref="IEngine"/>
/// and <see cref="Engine"/>. Override the lifecycle hooks to author the app; hand an instance to
/// <see cref="Run"/> and the platform host (desktop window or browser canvas) owns the window/device bring-up
/// and the frame loop:
/// <code>
/// public sealed class MyApp : LoomApplication
/// {
///     protected override void LoadContent()
///     {
///         Engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
///         SetGraph(BuildGraph());
///     }
///     protected override void Update(FrameTime time) { /* publish view-projection etc. */ }
/// }
/// // Program.cs (one line, shared by every platform):
/// LoomApplication.Run(new MyApp(), args);
/// </code>
/// The per-frame submission (<see cref="IEngine.Render"/>) is framework-owned: the hooks only feed it. Under the
/// default <see cref="RenderMode.Continuous"/> it happens every frame; under <see cref="RenderMode.OnDemand"/> it
/// is gated on <see cref="Invalidate"/> / first-frame / resize.
/// </summary>
public abstract class LoomApplication : IHostedApplication
{
	/// <summary>
	/// The application handed to the most recent <see cref="Run"/> call. The browser host reads it from its
	/// JS-driven start path (the runtime's module initializers are unreliable under WASM AOT, so the host can't
	/// rely on the registry to find the instance). Null before <see cref="Run"/> is called.
	/// </summary>
	public static LoomApplication? Current { get; private set; }

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

	/// <summary>The spiral-of-death guard: at most this many <see cref="FixedUpdate"/> steps run per frame before any further backlog is dropped.</summary>
	private const int MaxCatchUpSteps = 5;

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
	/// Draw calls are not issued here. Loom is graph-based: a pass's closures are registered once (in the graph built
	/// in <see cref="LoadContent"/>) and the engine records them every frame whether or not this is overridden. To
	/// draw something, add a pass to the graph.
	/// </para>
	/// </summary>
	protected virtual void BeforeRender(FrameTime time) { }

	/// <summary>
	/// Runs at a constant rate (<c>1 / AppOptions.FixedStepHz</c>) independent of the display's frame rate — zero,
	/// one, or several times per frame depending on how the actual frame time lines up with the fixed step (a
	/// spiral-of-death guard caps the catch-up at <see cref="MaxCatchUpSteps"/> steps per frame, dropping any
	/// further backlog rather than falling ever further behind). Never called while
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

	/// <summary>Release app-owned resources before the host tears down the engine. Runs once, on close, before <see cref="Engine"/> is disposed. Empty by default.</summary>
	protected virtual void UnloadContent() { }

	/// <summary>Make <paramref name="graph"/> the active graph (see <see cref="IEngine.SetGraph"/>). Call from <see cref="LoadContent"/>, or later to swap graphs at runtime.</summary>
	protected void SetGraph(RenderGraph graph) => Engine.SetGraph(graph);

	/// <summary>
	/// Ask the host to end the run — the desktop host closes the window; the web host stops its
	/// requestAnimationFrame loop. Safe to call from any lifecycle hook once <see cref="Engine"/> is bound.
	/// </summary>
	protected void Exit() => _host.RequestExit();

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

	void IHostedApplication.QueueInput(in InputEvent e) => _inputQueue.Enqueue(in e);

	RumbleQueue IHostedApplication.Rumble => _inputStack.RumbleQueue;

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
			while (_fixedAccumulator >= step && steps < MaxCatchUpSteps)
			{
				FixedUpdate(new FixedTime(step, _fixedTotalSeconds));
				_fixedAccumulator -= step;
				_fixedTotalSeconds += step;
				steps++;
			}
			if (_fixedAccumulator >= step)
				_fixedAccumulator = 0; // spiral-of-death guard: drop the backlog rather than keep falling further behind
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
	/// Run <paramref name="app"/> on the platform host. Reference a <c>Loom.Hosting.*</c> package so a host is
	/// present — the RID picks it; the Loom SDK adds the right one and a module initializer registers it. On
	/// desktop this blocks until the window closes. On the browser it returns promptly: there is no host to
	/// dispatch to synchronously (the JS side supplies the canvas and starts the loop), so the app is published
	/// as <see cref="Current"/> for the browser host's JS-driven start path to pick up.
	/// </summary>
	public static void Run(LoomApplication app, string[] args)
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
