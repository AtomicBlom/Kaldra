namespace Loom.Core.Hosting;

/// <summary>
/// Startup configuration a <see cref="LoomApplication"/> fills in its <c>Configure</c> override, read by the
/// platform host before it brings up the window/canvas and engine. Platform-neutral: the desktop host uses
/// <see cref="Width"/>×<see cref="Height"/> as the window size; the web host uses them only as a fallback when
/// the canvas's own display size is unavailable (the browser canvas normally dictates the size). The web
/// canvas <em>element</em> is never named here — it flows in from the JS side that owns the page.
/// </summary>
public sealed class AppOptions
{
	/// <summary>Window title (desktop) / document title hint. Ignored by an embedder that owns its own page.</summary>
	public string Title { get; set; } = "Loom";

	/// <summary>Initial width in pixels (desktop window size; web fallback size).</summary>
	public int Width { get; set; } = 1280;

	/// <summary>Initial height in pixels (desktop window size; web fallback size).</summary>
	public int Height { get; set; } = 720;
}
