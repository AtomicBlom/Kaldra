using System.Numerics;
using System.Runtime.InteropServices;

namespace Loom.Web.Demos;

/// <summary>
/// The per-draw uniform block the ported demos feed their mesh techniques: an object's model matrix plus a picking
/// id. A content contract with the shaders' <c>struct PerDraw { float4x4 model; uint objectId; }</c> — 80 bytes (a
/// <c>float4x4</c> is 64, the <c>uint</c> rounds up to a 16-byte boundary), so the typed struct carries the same
/// trailing padding. Ported verbatim from the sandbox (the shaders are shared).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 80)]
internal struct PerDraw
{
	/// <summary>The object's model-to-world matrix (row-major; the cbuffer packs its transpose, so the shader applies it column-form).</summary>
	public Matrix4x4 Model;

	/// <summary>The object's picking id (0 = untagged).</summary>
	public uint ObjectId;
}
