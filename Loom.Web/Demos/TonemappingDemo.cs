using System.Numerics;

using Loom.Concepts;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Web.Demos;

/// <summary>
/// Auto-exposure + tone-mapping, hand-ported from the sandbox. The lit scene is HDR; a compute pass meters its
/// average luminance and read-writes a 1×1 <em>persistent</em> R32F exposure value (it carries across frames, so the
/// adaptation is temporal); the tone-map then scales by that exposure and rolls the highlights off to the display.
/// Exercises the GPU→persistent→GPU loop the engine derives — specifically the WebGPU backend's r32float
/// read_write storage texture, a persistent resource that survives across frames, and sampling that R32F texture in
/// the following fullscreen pass.
/// <para>
/// The adaptation eases at ~8·dt per frame, so by the capture frame (60) the exposure has converged to the
/// scene-luminance target independent of the persistent's uninitialised starting value — making the frozen capture
/// deterministic and directly comparable with the Vulkan reference.
/// </para>
/// </summary>
internal sealed class TonemappingDemo : IWebDemo
{
	private readonly IEngine _engine;
	private readonly Camera _camera;
	private readonly Mesh _cube;
	private readonly Mesh _plane;
	private ConstantsHandle _viewproj = null!;
	private float _aspect = 16f / 9f;

	public TonemappingDemo(IEngine engine)
	{
		_engine = engine;
		_camera = new Camera(engine);
		var (cubeVerts, cubeIndices) = DemoGeometry.BuildCube();
		var (planeVerts, planeIndices) = DemoGeometry.BuildPlane();
		_cube = engine.CreateMesh(cubeVerts, cubeIndices);
		_plane = engine.CreateMesh(planeVerts, planeIndices);
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
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
			var lit = g.Screen.Color("lit", Format.Rgba16F);
			var depth = g.Screen.Depth("depth", Format.Depth32F);
			var meter = g.Domain("meter", 1, 1);
			var exposure = meter.Persistent("exposure", Format.R32F);
			var graded = g.Screen.Color("graded", Format.Rgba16F);
			_viewproj = g.Constants<Matrix4x4>("tonemapping.viewproj");

			g.Pass("scene")
				.Reads(_viewproj)
				.Writes(lit)
				.WritesDepth(depth)
				.Draw(ctx =>
				{
					DrawMesh(ctx, _cube, Matrix4x4.Identity);
					DrawMesh(ctx, _plane, Matrix4x4.Identity);
				});

			// Meter the lit scene's luminance into the 1×1 persistent exposure (the rate rides the push).
			g.Compute("exposure")
				.Samples(lit)
				.ReadWrites(exposure)
				.Dispatch(ctx => ctx.Dispatch("exposure", (1, 1, 1), [8f * ctx.DeltaTime], [lit], [exposure]));

			// Scale by exposure + roll off highlights (the browser backbuffer is SDR, so always the SDR path).
			g.Pass("tonemap")
				.Samples(lit, exposure)
				.Writes(graded)
				.Draw(ctx => ctx.FullScreen("tonemap", [ctx.HdrOutput ? 1f : 0f], lit, exposure));

			g.Pass("blit").Samples(graded).Writes(screen).Draw(ctx => ctx.FullScreen("blit", graded));
			g.Present(screen);
		});
	}

	public void Resize(int width, int height) => _aspect = height > 0 ? width / (float)height : _aspect;

	public void Update(float time)
	{
		DemoCamera.Orbit(_camera, time, _aspect);
		_engine.Write(_viewproj, _camera.ViewProjection);
	}

	private static void DrawMesh(IRenderContext ctx, Mesh mesh, in Matrix4x4 model)
	{
		var call = new DrawCall { Mesh = mesh, Technique = "lit" };
		call.SetUniforms(new PerDraw { Model = model, ObjectId = 0 });
		ctx.Draw(call);
	}
}
