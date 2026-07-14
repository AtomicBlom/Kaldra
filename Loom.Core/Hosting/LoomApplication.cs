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
///     protected override void Update(AppTime time) { /* publish view-projection etc. */ }
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

	/// <summary>Set startup options (title, size). Runs once, before the window/canvas + engine exist.</summary>
	protected virtual void Configure(AppOptions options) { }

	/// <summary>Define techniques, upload meshes/textures/materials, and <see cref="SetGraph"/>. Runs once, after <see cref="Engine"/> is bound.</summary>
	protected virtual void LoadContent() { }

	/// <summary>Per-frame CPU work: publish view-projection / per-object data into the graph's app-writable buffers via <c>Engine.Write</c>. Runs before <see cref="Draw"/> and the frame's render.</summary>
	protected virtual void Update(AppTime time) { }

	/// <summary>
	/// Supplementary per-frame hook that runs immediately <em>before</em> the framework's always-happens
	/// <see cref="IEngine.Render"/>. This is <b>not</b> where you issue draw calls — Loom is graph-based: pass
	/// closures are registered once (in the graph you build in <see cref="LoadContent"/>) and the engine submits
	/// every frame whether or not this is overridden. Use it for the work that must run right before submission
	/// with the frame's final state resolved: projecting a pointer hit, compositing an in-world UI panel, writing
	/// debug overlay lines. If you're looking for "the render callback", build a render graph instead.
	/// </summary>
	protected virtual void Draw(AppTime time) { }

	/// <summary>Make <paramref name="graph"/> the active graph (see <see cref="IEngine.SetGraph"/>). Call from <see cref="LoadContent"/>, or later to swap graphs at runtime.</summary>
	protected void SetGraph(RenderGraph graph) => Engine.SetGraph(graph);

	AppOptions IHostedApplication.ReadOptions()
	{
		var options = new AppOptions();
		Configure(options);
		return options;
	}

	void IHostedApplication.Load(IEngine engine)
	{
		Engine = engine;
		LoadContent();
	}

	void IHostedApplication.Tick(AppTime time)
	{
		Update(time);
		Draw(time);
		Engine.Render(new FrameInputs { DeltaSeconds = time.DeltaSeconds, Time = time.TotalSeconds });
	}

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
