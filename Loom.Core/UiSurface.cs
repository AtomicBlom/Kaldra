namespace Loom.Core;

/// <summary>Well-known <see cref="UiSurfaceId"/>s. The overlay HUD is surface 0; in-world quads take higher ids.</summary>
public static class UiSurface
{
	/// <summary>The screen-space overlay surface (the HUD), composited at native resolution over the backbuffer.</summary>
	public static readonly UiSurfaceId Hud = new(0);
}