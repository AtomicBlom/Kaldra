using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

LoomApplication.Run(new TriangleApp(), args);

/// <summary>
/// Your Loom application. The same source runs on the desktop (Vulkan) and in the browser (WebGPU) — the build's
/// RID picks the host, so there is no window/canvas or platform code to write. Override the lifecycle hooks:
/// <c>Configure</c> for startup options, <c>LoadContent</c> to define techniques and build the render graph,
/// <c>Update</c> for per-frame CPU work.
/// </summary>
internal sealed class TriangleApp : LoomApplication
{
	protected override void Configure(AppOptions options) => options.Title = "TriangleApp";

	protected override void LoadContent()
	{
		Engine.DefineTechnique("triangle", new TechniqueDescriptor("triangle"));

		// One pass: fabricate a triangle from SV_VertexID straight onto the backbuffer (no mesh, no camera).
		SetGraph(RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			g.Pass("triangle")
				.Writes(screen)
				.Draw(ctx => ctx.FullScreen("triangle"));
			g.Present(screen);
		}));
	}
}
