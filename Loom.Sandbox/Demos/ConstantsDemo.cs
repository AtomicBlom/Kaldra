using System.Numerics;
using System.Runtime.InteropServices;

using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// The app-writable parameter buffer (<see cref="GraphBuilder.Constants{T}"/>) shown end to end: the graph
/// declares a <see cref="TintParams"/> buffer, a fullscreen pass <c>.Reads</c> it and fills the screen from it,
/// and the host writes the colour each frame by handle (<see cref="IEngine.Write"/>). No graph rebuild — the
/// closure captures only the stable handle, and the bytes the shader reads change out-of-band. The colour is a
/// distinctive non-default orange so a flat-black frame would mean the write never reached the shader.
/// </summary>
internal sealed class ConstantsDemo : IDemo
{
	[StructLayout(LayoutKind.Sequential)]
	private readonly record struct TintParams(Vector4 Color);

	private IEngine _engine = null!;
	private ConstantsHandle _tint = null!;

	public string Name => "Constants param";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("tint", new TechniqueDescriptor("tint"));
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		_tint = g.Constants<TintParams>("tint.params");
		g.Pass("tint")
			.Reads(_tint)
			.Writes(screen)
			.Draw(ctx => ctx.FullScreen("tint"));
		g.Present(screen);
	});

	// The host-push channel: write the parameter buffer each frame (a static colour here; an animated effect would
	// vary it). The last write persists, so this is also correct as a write-once-on-change call.
	public void Update(Camera camera) =>
		_engine.Write(_tint, new TintParams(new Vector4(0.85f, 0.35f, 0.10f, 1f)));
}
