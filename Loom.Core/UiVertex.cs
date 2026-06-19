using System.Numerics;
using System.Runtime.InteropServices;

namespace Loom.Core;

/// <summary>
/// One UI vertex in the neutral draw stream produced by the default compositor (PlanningUI.md §D2.5). Positions
/// are in surface pixels (already post-transform, post-DPI), so the backend's UI renderer only applies the
/// pixel→clip scale+translate (the ImGui vertex push). Backend-neutral (<c>System.Numerics</c> + <see cref="Half4"/>),
/// <see cref="StructLayout"/> sequential so the layout matches what the backend uploads.
/// </summary>
/// <param name="Position">Surface-pixel position (post-transform, post-DPI).</param>
/// <param name="Uv">Atlas UV (glyph/image) or local rect-space coordinate (for the analytic SDF rounding).</param>
/// <param name="Color">Premultiplied RGBA16F colour (carries above-paper-white HDR + wide gamut).</param>
/// <param name="Params">Per-primitive scratch: rounded-rect radii / border half-width / SDF range (kind-dependent).</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct UIVertex(Vector2 Position, Vector2 Uv, Half4 Color, Vector4 Params);
