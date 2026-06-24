using System.Numerics;
using System.Runtime.InteropServices;
using Loom.Core.Geometry;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Concepts;

/// <summary>
/// Submits mesh draws, packing each draw's model matrix + object id into the per-draw uniform block — the second
/// uniform buffer a mesh technique reads (the first being a view-projection a pass <c>.Reads</c>). This is the
/// "place an object" path once the engine no longer auto-pushes a model: the app states the transform and id, the
/// engine delivers them as data. One reusable backing block is shared across draws (each <c>ctx.Draw</c> uploads
/// it synchronously), so a pass can place many objects without per-draw allocation.
/// </summary>
public sealed class DrawSubmitter
{
	// The per-draw block layout the mesh shaders read: float4x4 model (16) + float4 misc (4), misc.x = object id.
	private const int ModelFloats = 16;
	private readonly float[] _block = new float[20];

	/// <summary>
	/// Draw <paramref name="mesh"/> with <paramref name="technique"/> at <paramref name="model"/>, tagging it with
	/// <paramref name="objectId"/> (for picking; 0 = untagged) and optionally a <paramref name="material"/>. The pass
	/// must <c>.Reads</c> the view's <see cref="ViewConstants"/> so the vertex shader has its view-projection.
	/// </summary>
	public void Draw(IRenderContext ctx, Mesh mesh, string technique, in Matrix4x4 model, float objectId = 0f, Material? material = null)
	{
		MemoryMarshal.Write(MemoryMarshal.AsBytes(_block.AsSpan(0, ModelFloats)), in model);
		_block[16] = objectId;
		_block[17] = 0f;
		_block[18] = 0f;
		_block[19] = 0f;
		ctx.Draw(new DrawCall { Mesh = mesh, Technique = technique, Material = material, Uniforms = _block });
	}
}
