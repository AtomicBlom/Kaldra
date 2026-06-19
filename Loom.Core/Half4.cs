using System.Numerics;
using System.Runtime.InteropServices;

namespace Loom.Core;

/// <summary>
/// A four-component 16-bit-float colour (RGBA16F), the neutral storage for a <see cref="UIVertex"/>'s
/// <em>premultiplied</em> colour. Half precision is what lets the composited UI carry values above paper white
/// (HDR) and a wide gamut without banding, while staying half the size of full floats in the vertex stream
/// (PlanningUI.md §Color/HDR, §D2.5). Backend-neutral (<c>System.Half</c>, no graphics-API types); the backend's
/// UI renderer uploads these eight bytes straight into its vertex buffer, the way ImGui uploads packed RGBA8.
/// </summary>
/// <param name="R">Red, premultiplied by alpha. <c>0..1</c> is SDR/paper-white range; <c>&gt;1</c> is above paper white.</param>
/// <param name="G">Green, premultiplied by alpha.</param>
/// <param name="B">Blue, premultiplied by alpha.</param>
/// <param name="A">Alpha (coverage).</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Half4(Half R, Half G, Half B, Half A)
{
	/// <summary>Fully transparent (all components zero) — the premultiplied "nothing".</summary>
	public static Half4 Zero => new((Half)0f, (Half)0f, (Half)0f, (Half)0f);

	/// <summary>
	/// Build a premultiplied <see cref="Half4"/> from straight (non-premultiplied) float RGBA — the common authoring
	/// path: the RGB channels are scaled by <paramref name="a"/> so alpha-over compositing is correct. Values above
	/// <c>1</c> are preserved (above paper white).
	/// </summary>
	public static Half4 FromStraight(float r, float g, float b, float a) =>
		new((Half)(r * a), (Half)(g * a), (Half)(b * a), (Half)a);

	/// <summary>Build a <see cref="Half4"/> from components that are already premultiplied (e.g. a folded group alpha).</summary>
	public static Half4 FromPremultiplied(float r, float g, float b, float a) =>
		new((Half)r, (Half)g, (Half)b, (Half)a);

	/// <summary>The premultiplied components as a full-precision <see cref="Vector4"/> (for math/tests).</summary>
	public Vector4 ToVector4() => new((float)R, (float)G, (float)B, (float)A);
}
