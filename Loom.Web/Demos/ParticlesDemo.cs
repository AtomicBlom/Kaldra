using System.Numerics;

using Loom.Concepts;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Web.Demos;

/// <summary>
/// GPU particles, hand-ported from the sandbox — the compute → vertex-buffer hand-off. A compute pass writes
/// particle positions into a storage buffer (a looping fountain driven by time); the render pass reads that same
/// buffer as per-instance vertex data and additively blends glowing sparks over the lit scene, which is then
/// blitted to the screen. Exercises the WebGPU compute Dispatch, a Buffer-kind resource (storage + vertex usage),
/// a GPU-instanced procedural draw, and additive blending.
/// </summary>
internal sealed class ParticlesDemo : IWebDemo
{
	private const int ParticleCount = 4096;

	private static readonly VertexLayout ParticlePosition = new(
		VertexInputRate.PerInstance,
		binding: 0,
		new VertexAttribute("POSITION", VertexFormat.Float4)); // position.xyz, life (1 at spawn → 0 at death)

	private readonly IEngine _engine;
	private readonly Camera _camera;
	private readonly Mesh _cube;
	private readonly Mesh _plane;
	private ConstantsHandle _viewproj = null!;
	private float _aspect = 16f / 9f;

	public ParticlesDemo(IEngine engine)
	{
		_engine = engine;
		_camera = new Camera(engine);
		var (cubeVerts, cubeIndices) = DemoGeometry.BuildCube();
		var (planeVerts, planeIndices) = DemoGeometry.BuildPlane();
		_cube = engine.CreateMesh(cubeVerts, cubeIndices);
		_plane = engine.CreateMesh(planeVerts, planeIndices);
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
		engine.DefineTechnique("particle.update", new TechniqueDescriptor("particle_update", VertexEntry: "main", FragmentEntry: "main", IsCompute: true, HasFragmentPush: true));
		engine.DefineTechnique("particle.render", new TechniqueDescriptor("particle_render", Blend: BlendMode.Additive, Instance: ParticlePosition));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
	}

	public RenderGraph Build(int width, int height)
	{
		_aspect = height > 0 ? width / (float)height : 16f / 9f;
		return RenderGraph.Define(g =>
		{
			g.BackbufferClear = new Vector4(0.10f, 0.12f, 0.16f, 1.0f);
			var screen = g.Backbuffer;
			var lit = g.Screen.Color("lit", Format.Rgba16F);
			var depth = g.Screen.Depth("depth", Format.Depth32F);
			var particles = g.Buffer("particles", ParticleCount * 16); // float4 per particle
			_viewproj = g.Constants<Matrix4x4>("particles.viewproj");

			g.Pass("scene")
				.Reads(_viewproj)
				.Writes(lit)
				.WritesDepth(depth)
				.Draw(ctx =>
				{
					DrawMesh(ctx, _cube, Matrix4x4.Identity);
					DrawMesh(ctx, _plane, Matrix4x4.Identity);
				});

			g.Compute("particle.update")
				.ReadWrites(particles)
				.Dispatch(ctx => ctx.Dispatch("particle.update", ((uint)(ParticleCount / 64), 1, 1), [ctx.Time], [], [particles]));

			g.Pass("particles")
				.Reads(_viewproj)
				.Blends(lit)
				.ReadsVertex(particles)
				.Draw(ctx => ctx.Draw(new DrawCall { Technique = "particle.render", VertexCount = 6, Instances = DrawCall.Gpu(particles, ParticleCount) }));

			g.Pass("blit").Samples(lit).Writes(screen).Draw(ctx => ctx.FullScreen("blit", lit));
			g.Present(screen);
		});
	}

	public void Resize(int width, int height) => _aspect = height > 0 ? width / (float)height : _aspect;

	public void Update(float time)
	{
		var angle = time * 0.4f;
		var eye = new Vector3(MathF.Sin(angle) * 4.0f, 2.2f, MathF.Cos(angle) * 4.0f);
		_camera.LookAt(eye, new Vector3(0, 0.5f, 0)).Perspective(MathF.PI / 4f, _aspect, 0.1f, 100f);
		_engine.Write(_viewproj, _camera.ViewProjection);
	}

	private static void DrawMesh(IRenderContext ctx, Mesh mesh, in Matrix4x4 model)
	{
		var call = new DrawCall { Mesh = mesh, Technique = "lit" };
		call.SetUniforms(new PerDraw { Model = model, ObjectId = 0 });
		ctx.Draw(call);
	}
}
