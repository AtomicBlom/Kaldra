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

	/// <summary>
	/// The text on the system clipboard, or null when it holds none or the host cannot read it. Defaults to null so a
	/// host without a clipboard need not implement it.
	/// </summary>
	string? GetClipboardText() => null;

	/// <summary>Put <paramref name="text"/> on the system clipboard. Defaults to a no-op so a host without a clipboard need not implement it.</summary>
	void SetClipboardText(string text) { }

	/// <summary>
	/// Offer the user a file to keep, for a host whose app can't write files where the user can reach them: the web host
	/// opens the browser's save dialog, or downloads the file where the browser allows no dialog. It must follow soon
	/// after the user's input that asked for it, which the browser requires. Returns false when the host offers no
	/// files, as a desktop host doesn't, its app writing its own; defaults to that.
	/// </summary>
	/// <param name="name">The file name suggested, with its extension.</param>
	/// <param name="contentType">Its media type, e.g. <c>application/zip</c>.</param>
	/// <param name="contents">The file's bytes.</param>
	bool SaveFile(string name, string contentType, ReadOnlySpan<byte> contents) => false;
}
