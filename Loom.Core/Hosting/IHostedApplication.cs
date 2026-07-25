using Loom.Input;

namespace Loom.Core.Hosting;

/// <summary>
/// The driving surface an <see cref="IAppHost"/> uses to run a <see cref="LoomApplication"/>: read its startup
/// options, hand it the live engine once the device is up, and tick it each frame. <see cref="LoomApplication"/>
/// implements this <em>explicitly</em>, so these framework-plumbing calls stay off an app author's own type —
/// the author sees only the overridable hooks and <see cref="LoomApplication.Engine"/>. A host obtains it by
/// casting the application it was handed (<c>(IHostedApplication)app</c>).
/// </summary>
public interface IHostedApplication
{
	/// <summary>Build the app's <see cref="AppOptions"/> (runs its <c>Configure</c> override). Called once before device bring-up.</summary>
	AppOptions ReadOptions();

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

	/// <summary>Advance one frame: the app's <c>Update</c>, then <c>Draw</c>, then the framework's own unconditional <see cref="IEngine.Render"/>.</summary>
	void Tick(FrameTime time);

	/// <summary>Run the app's <c>UnloadContent</c> hook. Called once, on close, before the host disposes the engine.</summary>
	void Unload();
}
