namespace BinaryVibrance.Kaldra.Core.Hosting;

/// <summary>
/// The app-to-host control channel a host injects into <see cref="KaldraApplication"/> at
/// <see cref="IHostedApplication.Load"/> time — the mechanism <see cref="KaldraApplication.Exit"/> uses to ask the
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
	/// How many physical pixels of the app's surface make one layout unit on this display: about 3 on a typical phone,
	/// 1 on a desktop monitor at 100%. A UI scales its layout by it (Vysara's <c>UILayer.LayoutScale</c>) so controls keep
	/// a physical size a finger or a cursor can hit. Read it each frame: it changes when the window moves to another
	/// display. Defaults to 1 for a host that does not know its display's density.
	/// </summary>
	float DisplayScale => 1f;

	/// <summary>
	/// The text on the system clipboard, or null when it holds none or the host cannot read it. Defaults to null so a
	/// host without a clipboard need not implement it. The browser host, which may not read the clipboard without
	/// asking, answers with the text of a paste into the page in this frame (Ctrl+V's comes in the same frame as its
	/// keydown), and null in any other.
	/// <para>
	/// Main thread only: the thread the host runs the app's frames on (its <see cref="KaldraApplication"/> callbacks), since
	/// the desktop host's windowing (GLFW) reaches the clipboard there and nowhere else. The desktop host throws
	/// <see cref="InvalidOperationException"/> for a call from any other thread.
	/// </para>
	/// </summary>
	string? GetClipboardText() => null;

	/// <summary>
	/// Put <paramref name="text"/> on the system clipboard. Returns false when the host has no clipboard to put it on, so
	/// an app can say the copy did not happen rather than let the player paste nothing; defaults to that, so a host without
	/// a clipboard need not implement it. True means the host took the text: the browser host writes it asynchronously,
	/// and the browser can still refuse it there. Main thread only, as <see cref="GetClipboardText"/> is.
	/// </summary>
	bool SetClipboardText(string text) => false;

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

	/// <summary>
	/// Whether <see cref="ShowFileDialog"/> shows the system's dialogs: the desktop host's do, through SDL 3, where it
	/// ships beside the app. A browser has no paths to give an app, and offers files through <see cref="SaveFile"/> and
	/// a page's own controls instead. Defaults to false.
	/// </summary>
	bool CanShowFileDialogs => false;

	/// <summary>
	/// Show one of the system's file dialogs, without blocking the frames: <paramref name="done"/> is called later, on the
	/// main thread between frames, with the paths chosen, none when the user cancelled, or null when the dialog couldn't be
	/// shown. Call it from the main thread. A host without dialogs (see <see cref="CanShowFileDialogs"/>) calls
	/// <paramref name="done"/> with null at once; that is the default.
	/// </summary>
	void ShowFileDialog(FileDialog dialog, Action<IReadOnlyList<string>?> done) => done(null);

	/// <summary>
	/// Whether <see cref="SetFullscreen"/> can have the app fill the screen. The browser host can where the page may ask
	/// (the Fullscreen API, which a phone's browser offers no menu item for, and an iframe allows only with
	/// <c>allowfullscreen</c>); the Android host is always fullscreen and the desktop host's window is the app's own, so
	/// neither offers it. Defaults to false.
	/// </summary>
	bool CanFullscreen => false;

	/// <summary>
	/// Whether the app fills the screen now, as far as the host knows. The user can leave it without the app (Esc, a
	/// phone's Back gesture), so this is read rather than remembered. Defaults to false.
	/// </summary>
	bool IsFullscreen => false;

	/// <summary>
	/// Ask the host to have the app fill the screen, or to stop. The browser asks the page, which must follow soon after
	/// the user's input that asked for it (as <see cref="SaveFile"/>), and fullscreen arrives a moment later, as a resize;
	/// on a phone it also lets the app run under the camera cutout, which the safe area then reports. Returns false when
	/// the host can't (see <see cref="CanFullscreen"/>); defaults to that.
	/// </summary>
	bool SetFullscreen(bool on) => false;

	/// <summary>
	/// Open a file of the app's packaged content (its <c>KaldraContent</c> items) by its path under the content root, with
	/// forward slashes, as <c>levels/1.json</c>: the desktop host reads the content folder beside the app, the Android host
	/// the APK's assets. Throws <see cref="FileNotFoundException"/> for a path the app does not have. A host that packs no
	/// content throws <see cref="NotSupportedException"/>.
	/// </summary>
	Stream OpenContent(string path) =>
		throw new NotSupportedException("This host does not read packaged content.");
}
