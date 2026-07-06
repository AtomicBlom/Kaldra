using System.Numerics;

using Loom.Concepts;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Web.Demos;

/// <summary>
/// Object highlighting (picking + jump-flood outline), hand-ported from the sandbox. The cube writes its object id
/// into a low-res id buffer; a seed pass marks the hovered object's pixels and a jump-flood ping-pong (steps 4→2→1)
/// propagates the nearest seed; a resolve pass turns nearest-seed distance into an emissive ring composited over the
/// lit scene, then a nearest blit upscales the fixed 320×180 world domain to the screen. The <c>(read, write)</c>
/// handle swap is the ping-pong idiom — two physical buffers swapped in a normal loop, the graph following the
/// handles. Exercises a fixed-resolution domain, <c>g.Readback</c>, a parameter buffer, and the JFA fullscreen chain.
/// <para>
/// The sandbox reads the id buffer back to the CPU to pick the hovered object from the cursor; a headless browser has
/// no cursor, so — matching the desktop <c>--capture</c> protocol, which forces the cube hovered — this always writes
/// the cube's id (1) as the hovered object, so the outline shows deterministically.
/// </para>
/// </summary>
internal sealed class HighlightingDemo : IWebDemo
{
	// The outline seed pass's parameters: the hovered object id (0 = nothing) in P.X. A single float4 row, the same
	// std140/cbuffer layout the shared outline_seed shader reads on every backend.
	private readonly record struct OutlineParams(Vector4 P);

	private const uint CubeId = 1;

	private readonly IEngine _engine;
	private readonly Camera _camera;
	private readonly Mesh _cube;
	private readonly Mesh _plane;
	private ConstantsHandle _viewproj = null!;
	private ConstantsHandle _hoverParams = null!;
	private float _aspect = 16f / 9f;

	public HighlightingDemo(IEngine engine)
	{
		_engine = engine;
		_camera = new Camera(engine);
		var (cubeVerts, cubeIndices) = DemoGeometry.BuildCube();
		var (planeVerts, planeIndices) = DemoGeometry.BuildPlane();
		_cube = engine.CreateMesh(cubeVerts, cubeIndices);
		_plane = engine.CreateMesh(planeVerts, planeIndices);
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
		engine.DefineTechnique("id", new TechniqueDescriptor("id", HasVertexInput: true));
		engine.DefineTechnique("outline.seed", new TechniqueDescriptor("outline_seed"));
		engine.DefineTechnique("outline.jfa", new TechniqueDescriptor("outline_jfa", HasFragmentPush: true));
		engine.DefineTechnique("outline.resolve", new TechniqueDescriptor("outline_resolve"));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
	}

	public RenderGraph Build(int width, int height)
	{
		_aspect = height > 0 ? width / (float)height : 16f / 9f;
		return RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var world = g.Domain("world", 320, 180);
			var lit = world.Color("lit", Format.Rgba16F);
			var depth = world.Depth("depth", Format.Depth32F);
			var ids = world.Color("object_id", Format.R16F);
			var seedA = world.Color("outline_a", Format.Rg16F);
			var seedB = world.Color("outline_b", Format.Rg16F);
			var outlined = world.Color("lit_outlined", Format.Rgba16F);
			_viewproj = g.Constants<Matrix4x4>("highlighting.viewproj");
			_hoverParams = g.Constants<OutlineParams>("outline.params");

			g.Pass("scene")
				.Reads(_viewproj)
				.Writes(lit)
				.WritesDepth(depth)
				.Draw(ctx =>
				{
					DrawMesh(ctx, _cube, "lit", Matrix4x4.Identity, 0);
					DrawMesh(ctx, _plane, "lit", Matrix4x4.Identity, 0);
				});

			// Write the cube's object id (1) and read it back so a host could decide the hovered object; here the
			// hovered id is forced to the cube (see WriteHover in Update), so the readback is exercised but unused.
			g.Pass("id").Reads(_viewproj).Writes(ids).Draw(ctx => DrawMesh(ctx, _cube, "id", Matrix4x4.Identity, CubeId));
			g.Readback(ids);

			g.Pass("outline.seed")
				.Samples(ids)
				.Reads(_hoverParams)
				.Writes(seedA)
				.Draw(ctx => ctx.FullScreen("outline.seed", ids));

			var (read, write) = (seedA, seedB);
			const int outlineReach = 4; // a 2-3px ring only needs distance out to its own width: steps 4→2→1
			for (var step = outlineReach; step >= 1; step >>= 1)
			{
				var src = read; // capture per-iteration so the closure follows this step's handle
				float jump = step;
				g.Pass($"outline.jfa{step}")
					.Samples(src)
					.Writes(write)
					.Draw(ctx => ctx.FullScreen("outline.jfa", [jump, world.Width, world.Height], src));
				(read, write) = (write, read); // swap; the graph follows the handles
			}

			var flood = read; // the final flood after the swaps
			g.Pass("outline.resolve")
				.Samples(flood, lit)
				.Writes(outlined)
				.Draw(ctx => ctx.FullScreen("outline.resolve", flood, lit));

			// Nearest upscale from the low-res world to the native screen.
			g.Pass("blit").Samples(outlined).Writes(screen).Draw(ctx => ctx.FullScreen("blit", outlined));
			g.Present(screen);
		});
	}

	public void Resize(int width, int height) => _aspect = height > 0 ? width / (float)height : _aspect;

	public void Update(float time)
	{
		DemoCamera.Orbit(_camera, time, _aspect);
		_engine.Write(_viewproj, _camera.ViewProjection);
		// Force the cube hovered so the outline is always visible (a headless page has no cursor to pick with).
		_engine.Write(_hoverParams, new OutlineParams(new Vector4(CubeId, 0f, 0f, 0f)));
	}

	// Pack the object's model matrix + picking id into the per-draw uniform block and submit under the given technique.
	private static void DrawMesh(IRenderContext ctx, Mesh mesh, string technique, in Matrix4x4 model, uint objectId)
	{
		var call = new DrawCall { Mesh = mesh, Technique = technique };
		call.SetUniforms(new PerDraw { Model = model, ObjectId = objectId });
		ctx.Draw(call);
	}
}
