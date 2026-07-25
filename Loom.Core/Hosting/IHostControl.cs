namespace Loom.Core.Hosting;

/// <summary>
/// The app-to-host control channel a host injects into <see cref="LoomApplication"/> at
/// <see cref="IHostedApplication.Load"/> time — the mechanism <see cref="LoomApplication.Exit"/> uses to ask the
/// host to end the run. An app never talks to the platform window/canvas directly, so this is the one hook that
/// flows from the app back to the host rather than the other way around.
/// </summary>
public interface IHostControl
{
	/// <summary>Ask the host to end the run: the desktop host closes the window; the web host stops the requestAnimationFrame loop.</summary>
	void RequestExit();

	/// <summary>
	/// Ask the host to schedule a render for the next frame. Only meaningful under
	/// <see cref="RenderMode.OnDemand"/>, where the host otherwise sleeps until an event arrives — the desktop host
	/// wakes its event loop. A no-op under <see cref="RenderMode.Continuous"/> or on a host that always renders.
	/// Safe to call from any thread. Defaults to a no-op so a host that does not support on-demand rendering need
	/// not implement it.
	/// </summary>
	void RequestRender() { }
}
