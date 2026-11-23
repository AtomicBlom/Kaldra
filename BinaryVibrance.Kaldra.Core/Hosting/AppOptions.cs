namespace BinaryVibrance.Kaldra.Core.Hosting;

/// <summary>
/// Startup configuration a <see cref="KaldraApplication"/> fills in its <c>Configure</c> override, read by the
/// platform host before it brings up the window/canvas and engine. Platform-neutral: the desktop host uses
/// <see cref="Width"/>×<see cref="Height"/> as the window size; the web host uses them only as a fallback when
/// the canvas's own display size is unavailable (the browser canvas normally dictates the size). The web
/// canvas <em>element</em> is never named here — it flows in from the JS side that owns the page.
/// </summary>
public sealed class AppOptions
{
	/// <summary>Window title (desktop) / document title hint. Ignored by an embedder that owns its own page.</summary>
	public string Title { get; set; } = "Kaldra";

	/// <summary>Initial width in pixels (desktop window size; web fallback size).</summary>
	public int Width { get; set; } = 1280;

	/// <summary>Initial height in pixels (desktop window size; web fallback size).</summary>
	public int Height { get; set; } = 720;

	/// <summary>
	/// Engine configuration handed to the backend at device bring-up (<see cref="Hosting.EngineSettings.VSync"/>,
	/// <see cref="Hosting.EngineSettings.HdrOutput"/>, <see cref="Hosting.EngineSettings.MaxFramesPerSecond"/>,
	/// <see cref="Hosting.EngineSettings.WatchShaderDirectory"/>, <see cref="Hosting.EngineSettings.EnableValidation"/>) —
	/// set members here in <c>Configure</c> rather than the host constructing settings itself. Defaults
	/// <see cref="Hosting.EngineSettings.EnableValidation"/> <b>off</b>: a hosted app is assumed to be a
	/// production build, so a developer opts into the validation/debug layer explicitly rather than shipping it on
	/// by default (constructing <see cref="Hosting.EngineSettings"/> directly, as the sandbox and tests do, still
	/// defaults it on). Pick the backend via <see cref="Hosting.EngineSettings.PreferredBackend"/> (null = the
	/// first one available); the host overwrites it with the backend it actually resolved, so this always ends
	/// up recording the truth even when left unset.
	/// </summary>
	public EngineSettings EngineSettings { get; set; } = new() { EnableValidation = false };

	/// <summary>
	/// How the host paces frame submission: <see cref="Hosting.RenderMode.Continuous"/> (the default) submits every
	/// tick, while <see cref="Hosting.RenderMode.OnDemand"/> submits only when the app calls
	/// <see cref="KaldraApplication.Invalidate"/>, on the first frame, or after a resize — for editors, tools, and
	/// UI-driven apps that should idle when nothing changes.
	/// </summary>
	public RenderMode RenderMode { get; set; } = RenderMode.Continuous;

	/// <summary>
	/// Fixed-timestep rate in Hz for <see cref="KaldraApplication.FixedUpdate"/> (0 = disabled, the default).
	/// Deterministic simulation/physics code that must not vary with the display's frame rate steps at
	/// <c>1 / FixedStepHz</c> seconds regardless of how fast frames arrive, with up to 5 catch-up steps per frame
	/// before any further backlog is dropped (the spiral-of-death guard). <see cref="FrameTime.Alpha"/> carries
	/// the leftover fraction for render interpolation between the last two fixed states.
	/// </summary>
	public double FixedStepHz { get; set; }
}
