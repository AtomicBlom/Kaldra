using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;

KaldraApplication.Run(new TriangleApp(), args);

/// <summary>
/// A minimal Kaldra application, whole: the entry point, the app, and its render graph in one file. No window, no
/// canvas, no <c>[JSImport]</c>, no platform <c>#if</c>. <c>dotnet build</c> yields a native (Vulkan) desktop
/// exe; <c>dotnet publish -r browser-wasm</c> yields a WebGPU browser app from this same source — the RID picks
/// the host, and <see cref="KaldraApplication.Run"/> hands off to it.
/// </summary>
internal sealed class TriangleApp : KaldraApplication
{
	protected override void Configure(AppOptions options) => options.Title = "Kaldra - Hello Triangle";

	protected override void LoadContent()
	{
		Engine.DefineTechnique("triangle", new TechniqueDescriptor("triangle"));

		// One pass: fabricate the triangle from SV_VertexID straight onto the backbuffer (no mesh, no camera).
		// FullScreen is the mesh-less fabricated-vertex path — it issues the 3-vertex non-indexed draw the
		// shader's SV_VertexID trick expects (ctx.Draw(DrawCall) with no mesh is only wired for GPU instancing).
		SetGraph(RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			g.Pass("triangle")
				.Writes(screen)
				.Draw(static ctx => ctx.FullScreen("triangle"));
			g.Present(screen);
		}));
	}
}
