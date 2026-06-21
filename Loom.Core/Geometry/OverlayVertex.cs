using System.Numerics;
using System.Runtime.InteropServices;
using Loom.Core.Primitives;

namespace Loom.Core.Geometry;

/// <summary>
/// The vertex of a 2D overlay draw list — the universal shape for screen-space, painter-ordered geometry
/// (UI, immediate-mode overlays, a textured quad): a pixel-space position, a texture coordinate, a
/// premultiplied colour, and a float4 of per-vertex data the consuming technique defines. Backend-neutral
/// (<c>System.Numerics</c> + <see cref="Half4"/>), <see cref="StructLayout"/> sequential so it uploads as
/// <see cref="VertexLayout.Overlay"/> verbatim.
/// </summary>
/// <param name="Position">Target-pixel position (already post-transform, post-DPI).</param>
/// <param name="Uv">Texture coordinate, or a local rect-space coordinate the technique interprets.</param>
/// <param name="Color">Premultiplied RGBA16F colour (carries above-paper-white HDR + wide gamut).</param>
/// <param name="Custom">Per-vertex, technique-defined data (the UI overlay uses it for rounded-rect/border/SDF parameters).</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct OverlayVertex(Vector2 Position, Vector2 Uv, Half4 Color, Vector4 Custom);
