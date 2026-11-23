using System.Numerics;
using System.Runtime.InteropServices;

namespace BinaryVibrance.Kaldra.Core.Geometry;

/// <summary>
/// Kaldra's canonical interleaved vertex: position, normal, UV, and a tangent. A single fixed layout — enough
/// to drive the reference frame's geometry plus normal mapping. Backend-neutral
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
