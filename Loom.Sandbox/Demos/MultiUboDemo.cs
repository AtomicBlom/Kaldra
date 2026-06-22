using System.Numerics;
using System.Runtime.InteropServices;

using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Two uniform buffers feeding one technique (multi-UBO), end to end: a fullscreen pass <c>.Reads</c> a host-written
/// <see cref="GraphBuilder.Constants{T}"/> base colour (bound to uniform slot 0) and the <c>FullScreen</c> call
/// supplies a second colour as an in-closure push (bound to slot 1); the shader multiplies them. This is the same
/// push+Constants coexistence the cascaded-shadow lighting needs (host-written params + closure-pushed matrices).
/// The product (0.4, 0.6, 0.2) is distinct from either factor, so a frame that matches it proves both buffers
/// reached the shader at the right slots.
/// </summary>
internal sealed class MultiUboDemo : IDemo
{
	[StructLayout(LayoutKind.Sequential)]
	private readonly record struct Tint(Vector4 Color);

	private IEngine _engine = null!;
	private ConstantsHandle _base = null!;

	public string Name => "Multi-UBO";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("tint2", new TechniqueDescriptor("tint2"));
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		_base = g.Constants<Tint>("tint2.base");
		g.Pass("tint2")
			.Reads(_base)
			.Writes(screen)
			.Draw(ctx => ctx.FullScreen("tint2", [0.5f, 1f, 0.5f, 1f])); // the push fills slot 1's uniform buffer
		g.Present(screen);
	});

	// Write the base colour each frame (slot 0); the multiplier rides the FullScreen push (slot 1). 0.8·0.5, 0.6·1,
	// 0.4·0.5 → (0.4, 0.6, 0.2).
	public void Update(Camera camera) =>
		_engine.Write(_base, new Tint(new Vector4(0.8f, 0.6f, 0.4f, 1f)));
}
