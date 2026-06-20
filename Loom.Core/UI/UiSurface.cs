namespace Loom.Core.UI;

/// <summary>Well-known <see cref="UISurfaceId"/>s. The overlay HUD is surface 0; in-world quads take higher ids.</summary>
public static class UISurface
{
	/// <summary>The screen-space overlay surface (the HUD), composited at native resolution over the backbuffer.</summary>
	public static readonly UISurfaceId Hud = new(0);
}
