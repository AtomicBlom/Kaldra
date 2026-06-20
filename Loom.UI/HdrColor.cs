using Loom.Core;

namespace Loom.UI;

/// <summary>
/// An authoring colour — float RGBA where <c>0..1</c> is SDR / paper-white range and a component <c>&gt;1</c> is
/// above paper white (opt-in HDR). Named <c>HdrColor</c> (not <c>Color</c>) for explicitness and to dodge the
/// <see cref="System.Drawing.Color"/> clash (PlanningUI.md §Color/HDR). Folded to a premultiplied
/// <see cref="Half4"/> by the compositor; the paper-white reference scale is applied in the shader (MU1).
/// </summary>
public readonly record struct HdrColor(float R, float G, float B, float A)
{
	public static HdrColor Transparent => new(0f, 0f, 0f, 0f);

	/// <summary>From 8-bit sRGB-ish channels (0..255) at full alpha — the common HUD authoring shorthand.</summary>
	public static HdrColor FromRgb(byte r, byte g, byte b, byte a = 255) => new(r / 255f, g / 255f, b / 255f, a / 255f);

	/// <summary>Fold in a group/animation opacity and convert to a premultiplied <see cref="Half4"/> for the draw stream.</summary>
	public Half4 ToPremultiplied(float opacity = 1f) => Half4.FromStraight(R, G, B, A * opacity);
}
