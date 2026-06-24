using System.Numerics;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;

namespace Loom.Concepts;

/// <summary>
/// A named view whose clip-corrected view-projection is published to a <see cref="GraphBuilder.Constants{T}"/>
/// buffer each frame, for a geometry pass to <c>.Reads</c> and its vertex shader to read as a uniform. This is how
/// the view reaches the GPU once the engine no longer auto-pushes an MVP: the app owns the <see cref="Camera"/>,
/// this composes the engine's <see cref="IEngine.ClipSpaceCorrection"/> so the result lands in clip space on every
/// backend, and the engine just delivers the bytes. Declare one per view a frame renders from (a main camera, a
/// sun, each cascade); <c>.Reads(<see cref="Handle"/>)</c> on the passes that draw from it.
/// </summary>
public sealed class ViewConstants
{
	/// <summary>The parameter buffer holding this view's clip-corrected view-projection — pass it to <c>.Reads</c>.</summary>
	public ConstantsHandle Handle { get; }

	/// <summary>Declare the view's constants buffer in <paramref name="graph"/> under <paramref name="name"/>.</summary>
	public ViewConstants(GraphBuilder graph, string name) =>
		Handle = graph.Constants<Matrix4x4>(name);

	/// <summary>
	/// Publish <paramref name="camera"/>'s view-projection for this frame, composed with the engine's clip-space
	/// convention. Call before <see cref="IEngine.Render"/> (e.g. from a demo's per-frame update); the last write
	/// persists, so write every frame for a moving view.
	/// </summary>
	public void Publish(IEngine engine, in Camera camera) =>
		engine.Write(Handle, camera.ViewProjectionMatrix * engine.ClipSpaceCorrection);
}
