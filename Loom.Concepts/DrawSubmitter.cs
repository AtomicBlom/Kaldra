using System.Numerics;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Concepts;

/// <summary>
/// Submits mesh draws, packing each draw's model matrix + object id into the per-draw uniform block (a
/// <see cref="PerDraw"/>) — the second uniform buffer a mesh technique reads (the first being a view-projection a
/// pass <c>.Reads</c>). This is the "place an object" path once the engine no longer auto-pushes a model: the app
/// states the transform and id, the engine delivers them as data. One reusable backing block is shared across
/// draws (each <c>ctx.Draw</c> uploads it synchronously), so a pass can place many objects without per-draw allocation.
/// </summary>
public sealed class DrawSubmitter
{
	// One reusable PerDraw, type-erased to bytes per submit; ctx.Draw uploads synchronously, so it can be reused.
	private readonly PerDraw[] _block = new PerDraw[1];

	/// <summary>
	/// Draw <paramref name="mesh"/> with <paramref name="technique"/> at <paramref name="model"/>, tagging it with
	/// <paramref name="objectId"/> (for picking; 0 = untagged) and optionally a <paramref name="material"/>. The pass
	/// must <c>.Reads</c> the view's <see cref="ViewConstants"/> so the vertex shader has its view-projection.
	/// </summary>
	public void Draw(IRenderContext ctx, Mesh mesh, string technique, in Matrix4x4 model, uint objectId = 0, Material? material = null)
	{
		_block[0] = new PerDraw { Model = model, ObjectId = objectId };
		ctx.Draw(new DrawCall { Mesh = mesh, Technique = technique, Material = material, Uniforms = DrawCall.Bytes(_block) });
	}

	/// <summary>
	/// Draw a compute-pre-skinned <paramref name="mesh"/> (its vertices supplied by <paramref name="skinnedVertices"/>,
	/// a <c>.ReadsVertex</c> buffer) with the same per-draw model + id block as <see cref="Draw"/>. The skinned
	/// vertices are already in object space, so <paramref name="model"/> places that object in the world.
	/// </summary>
	public void DrawSkinned(IRenderContext ctx, Mesh mesh, BufferHandle skinnedVertices, string technique, in Matrix4x4 model, uint objectId = 0)
	{
		_block[0] = new PerDraw { Model = model, ObjectId = objectId };
		ctx.Draw(new DrawCall { Mesh = mesh, SkinnedVertices = skinnedVertices, Technique = technique, Uniforms = DrawCall.Bytes(_block) });
	}
}
