using System.Numerics;
using System.Runtime.InteropServices;

using Loom.Concepts;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Sandbox.Demos; // linked DemoSun + SandboxVertexLayouts + BloomGraph (g.Bloom)

namespace Loom.Web.Demos;

/// <summary>
/// The full reference frame, hand-ported from the sandbox — every capability composed into one graph. The whole 3D
/// world renders into a small 320×180 domain (deferred g-buffer → shadows → SSAO → lighting → forward transparency →
/// GPU particles → JFA hover outline → bloom → auto-exposure → tone-map), then a nearest upscale blows it up to the
/// native screen. This is the acceptance gate: it exercises the entire WebGPU backend at once, with the engine
/// deriving all ordering, the colour-attachment↔shader-read flips, depth states, load/store, aliasing, and present.
/// The sandbox HUD is dropped (draw lists land with the UI track); the cube is forced hovered so the outline shows.
/// </summary>
internal sealed class FullReferenceDemo : IWebDemo
{
	private readonly record struct OutlineParams(Vector4 P);

	private const int ParticleCount = 4096;
	private const uint CubeId = 1;

	private readonly IEngine _engine;
	private readonly Camera _camera;
	private readonly Camera _sunCam;
	private readonly Mesh _cube;
	private readonly Mesh _plane;
	private ConstantsHandle _viewproj = null!;
	private ConstantsHandle _sunViewproj = null!;
	private ConstantsHandle _hoverParams = null!;
	private float _aspect = 16f / 9f;

	public FullReferenceDemo(IEngine engine)
	{
		_engine = engine;
		_camera = new Camera(engine);
		_sunCam = DemoSun.View(engine);
		var (cubeVerts, cubeIndices) = DemoGeometry.BuildCube();
		var (planeVerts, planeIndices) = DemoGeometry.BuildPlane();
		_cube = engine.CreateMesh(cubeVerts, cubeIndices);
		_plane = engine.CreateMesh(planeVerts, planeIndices);

		engine.DefineTechnique("depthonly", new TechniqueDescriptor("depthonly", HasVertexInput: true));
		engine.DefineTechnique("gbuffer", new TechniqueDescriptor("gbuffer", HasVertexInput: true));
		engine.DefineTechnique("ssao", new TechniqueDescriptor("ssao", HasFragmentPush: true));
		engine.DefineTechnique("ssao.blur", new TechniqueDescriptor("ssao_blur"));
		engine.DefineTechnique("lighting", new TechniqueDescriptor("lighting", HasFragmentPush: true));
		engine.DefineTechnique("transparent", new TechniqueDescriptor("transparent", HasVertexInput: true, Blend: BlendMode.AlphaOver));
		engine.DefineTechnique("particle.update", new TechniqueDescriptor("particle_update", VertexEntry: "main", FragmentEntry: "main", IsCompute: true, HasFragmentPush: true));
		engine.DefineTechnique("particle.render", new TechniqueDescriptor("particle_render", Blend: BlendMode.Additive, Instance: SandboxVertexLayouts.ParticlePosition));
		engine.DefineTechnique("id", new TechniqueDescriptor("id", HasVertexInput: true));
		engine.DefineTechnique("outline.seed", new TechniqueDescriptor("outline_seed"));
		engine.DefineTechnique("outline.jfa", new TechniqueDescriptor("outline_jfa", HasFragmentPush: true));
		engine.DefineTechnique("outline.resolve", new TechniqueDescriptor("outline_resolve"));
		engine.DefineTechnique("bloom.prefilter", new TechniqueDescriptor("bloom_prefilter", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("bloom.down", new TechniqueDescriptor("bloom_sample", LinearFilter: true));
		engine.DefineTechnique("bloom.up", new TechniqueDescriptor("bloom_sample", Blend: BlendMode.Additive, LinearFilter: true));
		engine.DefineTechnique("bloom.composite", new TechniqueDescriptor("bloom_composite", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("exposure", new TechniqueDescriptor("exposure", VertexEntry: "main", FragmentEntry: "main", IsCompute: true, HasFragmentPush: true));
		engine.DefineTechnique("tonemap", new TechniqueDescriptor("tonemap", HasFragmentPush: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
	}

	public RenderGraph Build(int width, int height)
	{
		_aspect = height > 0 ? width / (float)height : 16f / 9f;
		return RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var world = g.Domain("world", 320, 180);
			var albedo = world.Color("albedo", Format.Rgba8Srgb);
			var normal = world.Color("normal", Format.Rgba16F);
			var position = world.Color("position", Format.Rgba16F);
			var depth = world.Depth("depth", Format.Depth32F);
			var ao = world.Color("ao", Format.R16F);
			var aoBlur = world.Color("ao_blur", Format.R16F);
			var lit = world.Color("lit", Format.Rgba16F);

			var sun = g.Domain("sun_shadow", 1024, 1024);
			var shadowMap = sun.Depth("sun_depth", Format.Depth32F);
			_viewproj = g.Constants<Matrix4x4>("fullref.viewproj");
			_sunViewproj = g.Constants<Matrix4x4>("fullref.sun.viewproj");

			g.Pass("shadow")
				.Reads(_sunViewproj)
				.WritesDepth(shadowMap)
				.Draw(ctx =>
				{
					DrawMesh(ctx, _cube, "depthonly", 0);
					DrawMesh(ctx, _plane, "depthonly", 0);
				});

			g.Pass("gbuffer")
				.Reads(_viewproj)
				.Writes(albedo, normal, position)
				.WritesDepth(depth)
				.Draw(ctx =>
				{
					DrawMesh(ctx, _cube, "gbuffer", 0);
					DrawMesh(ctx, _plane, "gbuffer", 0);
				});

			g.Pass("ssao")
				.Samples(position, normal)
				.Writes(ao)
				.Draw(ctx => ctx.FullScreen("ssao", Mat(_camera.ViewProjection), position, normal));

			g.Pass("ssao.blur")
				.Samples(ao)
				.Writes(aoBlur)
				.Draw(ctx => ctx.FullScreen("ssao.blur", ao));

			g.Pass("lighting")
				.Samples(albedo, normal, position, shadowMap, aoBlur)
				.Writes(lit)
				.Draw(ctx => ctx.FullScreen("lighting", Mat(_sunCam.ViewProjection), albedo, normal, position, shadowMap, aoBlur));

			g.Pass("transparent")
				.Reads(_viewproj)
				.Blends(lit)
				.ReadsDepth(depth)
				.Draw(ctx => DrawMesh(ctx, _cube, "transparent", 0));

			var particles = g.Buffer("particles", ParticleCount * 16);

			g.Compute("particle.update")
				.ReadWrites(particles)
				.Dispatch(ctx => ctx.Dispatch("particle.update", ((uint)(ParticleCount / 64), 1, 1), [ctx.Time], [], [particles]));

			g.Pass("particle.draw")
				.Reads(_viewproj)
				.Blends(lit)
				.ReadsVertex(particles)
				.Draw(ctx => ctx.Draw(new DrawCall { Technique = "particle.render", VertexCount = 6, Instances = DrawCall.Gpu(particles, ParticleCount) }));

			var ids = world.Color("object_id", Format.R16F);
			var seedA = world.Color("outline_a", Format.Rg16F);
			var seedB = world.Color("outline_b", Format.Rg16F);
			var outlined = world.Color("lit_outlined", Format.Rgba16F);

			_hoverParams = g.Constants<OutlineParams>("outline.params");
			g.Pass("id").Reads(_viewproj).Writes(ids).Draw(ctx => DrawMesh(ctx, _cube, "id", CubeId));
			g.Readback(ids);

			g.Pass("outline.seed")
				.Samples(ids)
				.Reads(_hoverParams)
				.Writes(seedA)
				.Draw(ctx => ctx.FullScreen("outline.seed", ids));

			var (read, write) = (seedA, seedB);
			for (var step = 4; step >= 1; step >>= 1)
			{
				var src = read;
				float jump = step;
				g.Pass($"outline.jfa{step}")
					.Samples(src)
					.Writes(write)
					.Draw(ctx => ctx.FullScreen("outline.jfa", [jump, world.Width, world.Height], src));
				(read, write) = (write, read);
			}

			var flood = read;
			g.Pass("outline.resolve")
				.Samples(flood, lit)
				.Writes(outlined)
				.Draw(ctx => ctx.FullScreen("outline.resolve", flood, lit));

			var glow = g.Bloom(outlined, threshold: 1.0f, mips: 5);

			var meter = g.Domain("meter", 1, 1);
			var exposure = meter.Persistent("exposure", Format.R32F);
			var graded = world.Color("graded", Format.Rgba16F);

			g.Compute("exposure")
				.Samples(lit)
				.ReadWrites(exposure)
				.Dispatch(ctx => ctx.Dispatch("exposure", (1, 1, 1), [8f * ctx.DeltaTime], [lit], [exposure]));

			g.Pass("tonemap")
				.Samples(glow, exposure)
				.Writes(graded)
				.Draw(ctx => ctx.FullScreen("tonemap", [ctx.HdrOutput ? 1f : 0f], glow, exposure));

			// Nearest upscale from the low-res world to the native screen.
			g.Pass("upscale").Samples(graded).Writes(screen).Draw(ctx => ctx.FullScreen("blit", graded));
			g.Present(screen);
		});
	}

	public void Resize(int width, int height) => _aspect = height > 0 ? width / (float)height : _aspect;

	public void Update(float time)
	{
		DemoCamera.Orbit(_camera, time, _aspect);
		_engine.Write(_viewproj, _camera.ViewProjection);
		_engine.Write(_sunViewproj, _sunCam.ViewProjection);
		_engine.Write(_hoverParams, new OutlineParams(new Vector4(CubeId, 0f, 0f, 0f)));
	}

	private static ReadOnlySpan<float> Mat(in Matrix4x4 m) => MemoryMarshal.CreateReadOnlySpan(in m.M11, 16);

	private static void DrawMesh(IRenderContext ctx, Mesh mesh, string technique, uint objectId)
	{
		var call = new DrawCall { Mesh = mesh, Technique = technique };
		call.SetUniforms(new PerDraw { Model = Matrix4x4.Identity, ObjectId = objectId });
		ctx.Draw(call);
	}
}
