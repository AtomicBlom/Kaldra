using System.Numerics;

using Loom.Core.Graph;

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
/// The per-frame submission (<see cref="IEngine.Render"/>) is framework-owned and always happens; the hooks
/// only feed it.
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

	private IHostControl _host = null!;
	private double _fixedStepHz;
	private double _fixedAccumulator;
	private double _fixedTotalSeconds;

	/// <summary>The spiral-of-death guard: at most this many <see cref="FixedUpdate"/> steps run per frame before any further backlog is dropped.</summary>
	private const int MaxCatchUpSteps = 5;

	/// <summary>Set startup options (title, size). Runs once, before the window/canvas + engine exist.</summary>
	protected virtual void Configure(AppOptions options) { }

	/// <summary>Define techniques, upload meshes/textures/materials, and <see cref="SetGraph"/>. Runs once, after <see cref="Engine"/> is bound.</summary>
	protected virtual void LoadContent() { }

	/// <summary>Per-frame CPU work: publish view-projection / per-object data into the graph's app-writable buffers via <c>Engine.Write</c>. Runs before <see cref="Draw"/> and the frame's render.</summary>
	protected virtual void Update(FrameTime time) { }

	/// <summary>
	/// Supplementary per-frame hook that runs immediately <em>before</em> the framework's always-happens
	/// <see cref="IEngine.Render"/>. This is <b>not</b> where you issue draw calls — Loom is graph-based: pass
	/// closures are registered once (in the graph you build in <see cref="LoadContent"/>) and the engine submits
	/// every frame whether or not this is overridden. Use it for the work that must run right before submission
	/// with the frame's final state resolved: projecting a pointer hit, compositing an in-world UI panel, writing
	/// debug overlay lines. If you're looking for "the render callback", build a render graph instead.
	/// </summary>
	protected virtual void Draw(FrameTime time) { }

	/// <summary>
	/// Runs at a constant rate (<c>1 / AppOptions.FixedStepHz</c>) independent of the display's frame rate — zero,
	/// one, or several times per frame depending on how the actual frame time lines up with the fixed step (a
	/// spiral-of-death guard caps the catch-up at <see cref="MaxCatchUpSteps"/> steps per frame, dropping any
	/// further backlog rather than falling ever further behind). Never called while
	/// <see cref="AppOptions.FixedStepHz"/> is 0 (the default). Put deterministic simulation/physics here rather
	/// than in <see cref="Update"/>; read <see cref="FrameTime.Alpha"/> in <see cref="Update"/>/<see cref="Draw"/>
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

	AppOptions IHostedApplication.ReadOptions()
	{
		var options = new AppOptions();
		Configure(options);
		_fixedStepHz = options.FixedStepHz;
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
		OnResize(width, height);
	}

	void IHostedApplication.SetFocus(bool focused) => OnFocusChanged(focused);

	void IHostedApplication.Tick(FrameTime time)
	{
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
		Draw(frameTime);
		Engine.Render(new FrameInputs { DeltaSeconds = time.DeltaSeconds, Time = time.TotalSeconds });
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
