using System.Numerics;
using System.Runtime.InteropServices;

namespace Loom.Core;

/// <summary>
/// Loom's canonical interleaved vertex: position, normal, UV, and a tangent. One fixed layout for now — enough
/// to drive the reference frame's geometry plus M11 normal mapping (Planning.md §6/§12 M5; PlanningPhase2.md
/// M11); per-technique vertex layouts (and skinning, instancing) can come later. Backend-neutral
/// (<c>System.Numerics</c>, no graphics-API types), so the backend owns the vertex-layout description that
/// mirrors these fields. Shaders that don't read the tangent simply ignore the extra attribute.
/// </summary>
/// <param name="Position">Object-space position.</param>
/// <param name="Normal">Object-space normal.</param>
/// <param name="Uv">Texture coordinate.</param>
/// <param name="Tangent">Object-space tangent (glTF convention): <c>xyz</c> is the tangent direction and
/// <c>w</c> is the ±1 bitangent handedness, so <c>bitangent = cross(normal, tangent.xyz) * tangent.w</c>.
/// Used to build the TBN frame for tangent-space normal mapping.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Vertex(Vector3 Position, Vector3 Normal, Vector2 Uv, Vector4 Tangent);
