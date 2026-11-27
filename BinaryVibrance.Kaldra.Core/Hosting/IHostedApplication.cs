using BinaryVibrance.Kaldra.Input;

namespace BinaryVibrance.Kaldra.Core.Hosting;

/// <summary>
/// The driving surface an <see cref="IAppHost"/> uses to run a <see cref="KaldraApplication"/>: read its startup
/// options, hand it the live engine once the device is up, and tick it each frame. <see cref="KaldraApplication"/>
/// implements this <em>explicitly</em>, so these framework-plumbing calls stay off an app author's own type —
/// the author sees only the overridable hooks and <see cref="KaldraApplication.Engine"/>. A host obtains it by
/// casting the application it was handed (<c>(IHostedApplication)app</c>).
/// </summary>
public interface IHostedApplication
{
	/// <summary>Build the app's <see cref="AppOptions"/> (runs its <c>Configure</c> override). Called once before device bring-up.</summary>
	AppOptions ReadOptions();

	/// <summary>
	/// Hand the app the command-line arguments the host did not consume as its own launch options (see
	/// <c>HostOptions.AppArgs</c>) — the app's own flags, verbatim and in order. Called once, before
	/// <see cref="ReadOptions"/>, so <c>Configure</c> can read them. Defaults to a no-op: a host with no argument
	/// surface (or an app that ignores args) need not implement it.
	/// </summary>
	void SetArgs(string[] appArgs) { }

	/// <summary>
	/// Bind the live engine and the app→host control channel, and run the app's <c>LoadContent</c> (define
	/// techniques, upload resources, set the graph). Called once, after the device is up.
	/// </summary>
	void Load(IEngine engine, IHostControl host);

	/// <summary>
	/// Notify the app of the current framebuffer size in pixels. Called once with the initial size before the
	/// first <see cref="Tick"/>, and again on every subsequent resize (skipped by the host while minimized).
	/// </summary>
	void Resize(int width, int height);

	/// <summary>Notify the app that the window/canvas gained or lost input focus.</summary>
	void SetFocus(bool focused);

	/// <summary>
	/// Notify the app that its frames are stopping: it has left the screen (minimized, sent to the background, the phone
	/// locked). The platform may end the process from here without warning, so this is the app's moment to save. A host
	/// calls <see cref="Resume"/> before the next frame it runs.
	/// </summary>
	void Suspend()
	{
	}

	/// <summary>Notify the app that its frames are starting again after <see cref="Suspend"/>.</summary>
	void Resume()
	{
	}

	/// <summary>
	/// The single host→app input channel: a host adapter feeds every raw device event through here (nothing
	/// ad-hoc), so desktop and web can't diverge. The app buffers the events and resolves them at the top of the
	/// next <see cref="Tick"/>. Called on the platform pump thread; single-threaded by design, so the buffer needs
	/// no locking.
	/// </summary>
	void QueueInput(in InputEvent e);

	/// <summary>
	/// The app's outbound rumble queue — the reverse direction of <see cref="QueueInput"/>. Game code enqueues
	/// effects onto it (via the input snapshot); a host adapter drains it to hardware each frame, a no-op where
	/// vibration is unsupported.
	/// </summary>
	RumbleQueue Rumble { get; }

	/// <summary>
	/// Whether the app took typed text on its last frame — an input layer holds a focused text field (see
	/// <see cref="BinaryVibrance.Kaldra.Input.InputContext.RequestTextInput"/>). A host with an on-screen keyboard shows it while this is
	/// true and hides it when it goes false; a host with a physical keyboard ignores it.
	/// </summary>
	bool WantsTextInput => false;

	/// <summary>
	/// Notify the app how much of the bottom of its surface the host's on-screen keyboard covers, in surface pixels: zero
	/// when it goes. Only a host with an on-screen keyboard calls it.
	/// </summary>
	void SetInputPaneHeight(float pixels)
	{
	}

	/// <summary>
	/// Notify the app how far in from each edge of its surface the screen hides content (a camera cutout), in surface
	/// pixels. Only a host whose screen hides part of the surface calls it; the app starts from none.
	/// </summary>
	void SetSafeArea(ScreenInsets insets)
	{
	}

	/// <summary>
	/// Notify the app that files or folders were dropped on its window, by their full paths, on the main thread. Only a
	/// host whose window takes drops calls it (the desktop host).
	/// </summary>
	void DropFiles(IReadOnlyList<string> paths)
	{
	}

	/// <summary>Advance one frame: the app's <c>Update</c>, then <c>Draw</c>, then the framework's own <see cref="IEngine.Render"/> (every frame under <see cref="RenderMode.Continuous"/>; gated under <see cref="RenderMode.OnDemand"/>).</summary>
	void Tick(FrameTime time);

	/// <summary>Run the app's <c>UnloadContent</c> hook. Called once, on close, before the host disposes the engine.</summary>
	void Unload();
}
