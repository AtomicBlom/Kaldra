using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace HelloTriangle;

/// <summary>
/// The whole application: register one technique and set one graph. No window, no canvas, no <c>[JSImport]</c>,
/// no platform <c>#if</c> — pure Loom code. <see cref="LoomApplication.Run"/> selects the desktop or browser
/// host from the build's RID, so this exact type runs on both.
/// </summary>
internal sealed class TriangleApp : LoomApplication
{
	protected override void Configure(AppOptions options) => options.Title = "Loom - Hello Triangle";

	protected override void LoadContent()
	{
		Engine.DefineTechnique("triangle", new TechniqueDescriptor("triangle"));
		SetGraph(TriangleGraph.Build());
	}
}
