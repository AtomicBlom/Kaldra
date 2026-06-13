using System.Numerics;
using System.Runtime.InteropServices;

namespace Loom.Core;

/// <summary>
/// Loom's canonical interleaved vertex: position, normal, and a UV. One fixed layout for now — enough to
/// drive the reference frame's geometry (Planning.md §6/§12 M5); per-technique vertex layouts (and skinning,
/// instancing) can come later. Backend-neutral (<c>System.Numerics</c>, no graphics-API types), so the
/// backend owns the binding/attribute description that mirrors these fields.
/// </summary>
/// <param name="Position">Object-space position.</param>
/// <param name="Normal">Object-space normal.</param>
/// <param name="Uv">Texture coordinate.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Vertex(Vector3 Position, Vector3 Normal, Vector2 Uv);
