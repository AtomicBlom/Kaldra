using Loom.Core.Graph;
using Loom.Core.Hosting;

namespace HelloTriangle;

/// <summary>
/// The one-pass graph: fabricate the triangle from <c>SV_VertexID</c> straight onto the backbuffer, no mesh, no
/// camera. Platform-neutral — compiled on both the desktop and browser flavors, no RID conditioning needed.
/// </summary>
internal static class TriangleGraph
{
	public static RenderGraph Build() => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		g.Pass("triangle")
			.Writes(screen)
			// FullScreen (not ctx.Draw(DrawCall)) is the mesh-less, fabricated-vertex path: ctx.Draw's own no-mesh
			// case is only wired for GPU-instanced draws (the particle path), not a plain single fabricated draw —
			// it force-unwraps DrawCall.Mesh unconditionally when Instances is empty. FullScreen issues exactly the
			// 3-vertex non-indexed draw this shader's SV_VertexID fabrication expects.
			.Draw(ctx => ctx.FullScreen("triangle"));
		g.Present(screen);
	});
}
