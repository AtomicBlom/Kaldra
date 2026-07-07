using System.Numerics;

using Loom.Concepts;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Sandbox.Demos; // the shared, linked BloomGraph fragment (g.Bloom)

namespace Loom.Web.Demos;

/// <summary>
/// The bloom sub-graph in isolation, hand-ported from the sandbox. The cube is drawn <em>emissive</em> — a bright
/// HDR source (~5×) — over a dim lit floor, so it visibly glows; the shared <c>g.Bloom</c> fragment (bright-pass →
/// mip-chain downsample → additive upsample → composite) spreads a halo from the bright pixels while the floor stays
/// dark. Exercises the WebGPU backend's linear-filtered fullscreen chain, per-mip half-resolution domains, additive
/// fullscreen blending, and the <c>.Blends</c> read-modify-write accumulation the upsample relies on.
/// </summary>
internal sealed class BloomDemo : IWebDemo
{
	private readonly IEngine _engine;
	private readonly Camera _camera;
	private readonly Mesh _cube;
	private readonly Mesh _plane;
	private ConstantsHandle _viewproj = null!;
	private float _aspect = 16f / 9f;

	public BloomDemo(IEngine engine)
	{
		_engine = engine;
		_camera = new Camera(engine);
		var (cubeVerts, cubeIndices) = DemoGeometry.BuildCube();
		var (planeVerts, planeIndices) = DemoGeometry.BuildPlane();
		_cube = engine.CreateMesh(cubeVerts, cubeIndices);
		_plane = engine.CreateMesh(planeVerts, planeIndices);
		engine.DefineTechnique("emissive", new TechniqueDescriptor("emissive", HasVertexInput: true)); // the glowing cube
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));            // the dim floor
		engine.DefineTechnique("bloom.prefilter", new TechniqueDescriptor("bloom_prefilter", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("bloom.down", new TechniqueDescriptor("bloom_sample", LinearFilter: true));
		engine.DefineTechnique("bloom.up", new TechniqueDescriptor("bloom_sample", Blend: BlendMode.Additive, LinearFilter: true));
		engine.DefineTechnique("bloom.composite", new TechniqueDescriptor("bloom_composite", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
	}

	public RenderGraph Build(int width, int height)
	{
		_aspect = height > 0 ? width / (float)height : 16f / 9f;
		return RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var hdr = g.Screen.Color("hdr", Format.Rgba16F);
			var depth = g.Screen.Depth("depth", Format.Depth32F);
			_viewproj = g.Constants<Matrix4x4>("bloom.viewproj");

			g.Pass("scene")
				.Reads(_viewproj)
				.Writes(hdr)
				.WritesDepth(depth)
				.Draw(ctx =>
				{
					DrawMesh(ctx, _plane, "lit", Matrix4x4.Identity);      // a dim floor for context (below the bloom threshold)
					DrawMesh(ctx, _cube, "emissive", Matrix4x4.Identity);  // the bright, glowing object
				});

			var glow = g.Bloom(hdr, threshold: 1.0f, mips: 5, intensity: 1.0f);

			g.Pass("blit").Samples(glow).Writes(screen).Draw(ctx => ctx.FullScreen("blit", glow));
			g.Present(screen);
		});
	}

	public void Resize(int width, int height) => _aspect = height > 0 ? width / (float)height : _aspect;

	public void Update(float time)
	{
		DemoCamera.Orbit(_camera, time, _aspect);
		_engine.Write(_viewproj, _camera.ViewProjection);
	}

	private static void DrawMesh(IRenderContext ctx, Mesh mesh, string technique, in Matrix4x4 model)
	{
		var call = new DrawCall { Mesh = mesh, Technique = technique };
		call.SetUniforms(new PerDraw { Model = model, ObjectId = 0 });
		ctx.Draw(call);
	}
}
