using System.Numerics;
using System.Runtime.InteropServices;

namespace Loom.Core.Animation;

/// <summary>
/// A skinned mesh's bind-pose vertex (M15): the canonical <see cref="Vertex"/> fields plus the two skinning
/// attributes — four bone indices and four blend weights. 80 bytes, sequentially packed, mirroring
/// <see cref="VertexLayout.MeshSkinned"/> (POSITION 0, NORMAL 12, TEXCOORD 24, TANGENT 32, JOINTS 48, WEIGHTS 64).
/// Backend-neutral (<c>System.Numerics</c> + plain <c>uint</c>s, no graphics-API types).
/// <para>
/// The joint indices reference the mesh's <see cref="Skeleton"/> joint array (and thus the bone-palette slot the
/// compute skinning pass multiplies by); the weights sum to 1 (the glTF importer renormalizes). A vertex bound to
/// fewer than four joints sets the unused weights to 0 (their joint index is then irrelevant).
/// </para>
/// </summary>
/// <param name="Position">Object-space bind-pose position.</param>
/// <param name="Normal">Object-space bind-pose normal.</param>
/// <param name="Uv">Texture coordinate.</param>
/// <param name="Tangent">Object-space tangent (glTF <c>xyz</c>+handedness convention), as in <see cref="Vertex"/>.</param>
/// <param name="Joint0">First bone index (into the skeleton's joint array).</param>
/// <param name="Joint1">Second bone index.</param>
/// <param name="Joint2">Third bone index.</param>
/// <param name="Joint3">Fourth bone index.</param>
/// <param name="Weights">The four blend weights matching <paramref name="Joint0"/>..<paramref name="Joint3"/>, summing to 1.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct SkinnedVertex(
	Vector3 Position,
	Vector3 Normal,
	Vector2 Uv,
	Vector4 Tangent,
	uint Joint0,
	uint Joint1,
	uint Joint2,
	uint Joint3,
	Vector4 Weights);
