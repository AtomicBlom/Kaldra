using System.Numerics;
using System.Runtime.InteropServices;

namespace Loom.Concepts;

/// <summary>
/// The per-draw block a mesh technique reads as its second uniform buffer (the first being a view-projection a
/// pass <c>.Reads</c>): an object's model matrix and its picking id. Submitted by <see cref="DrawSubmitter"/> and
/// matched by the shader-side <c>struct PerDraw { float4x4 model; uint objectId; }</c>. Sized to 80 bytes so it
/// matches that constant buffer's layout — a <c>float4x4</c> (64) plus a <c>uint</c> rounds up to a 16-byte
/// boundary, so the typed struct must carry the same trailing padding the cbuffer reserves.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 80)]
public struct PerDraw
{
	/// <summary>The object's model-to-world matrix (row-major; the cbuffer packs its transpose, so the shader applies it column-form).</summary>
	public Matrix4x4 Model;

	/// <summary>The object's picking id (written to the id target by the id technique); 0 = untagged.</summary>
	public uint ObjectId;
}
