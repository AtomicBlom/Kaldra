using System.Numerics;
using System.Runtime.InteropServices;

using Loom.Concepts;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Web.Demos;

/// <summary>
/// Screen-space ambient occlusion, hand-ported from the sandbox. A g-buffer pass writes world position + normal (and
/// albedo); the <c>ssao</c> pass samples them and writes a per-pixel occlusion term; <c>ssao.blur</c> smooths its
/// rotation noise; <c>ssao_view</c> shows the albedo darkened by that term, so the contact shadows (where the cube
/// meets the floor, in concave corners) read directly. Exercises the WebGPU backend's multi-render-target pass
/// (three colour attachments + depth) and a fullscreen pass whose push is the full camera view-projection matrix.
/// </summary>
internal sealed class SsaoDemo : IWebDemo
{
	private readonly IEngine _engine;
	private readonly Camera _camera;
	private readonly Mesh _cube;
	private readonly Mesh _plane;
	private ConstantsHandle _viewproj = null!;
	private float _aspect = 16f / 9f;

	public SsaoDemo(IEngine engine)
	{
		_engine = engine;
		_camera = new Camera(engine);
		var (cubeVerts, cubeIndices) = DemoGeometry.BuildCube();
		var (planeVerts, planeIndices) = DemoGeometry.BuildPlane();
		_cube = engine.CreateMesh(cubeVerts, cubeIndices);
		_plane = engine.CreateMesh(planeVerts, planeIndices);
		engine.DefineTechnique("gbuffer", new TechniqueDescriptor("gbuffer", HasVertexInput: true));
		engine.DefineTechnique("ssao", new TechniqueDescriptor("ssao", HasFragmentPush: true)); // camera VP rides the push
		engine.DefineTechnique("ssao.blur", new TechniqueDescriptor("ssao_blur"));
		engine.DefineTechnique("ssao_view", new TechniqueDescriptor("ssao_view"));
	}

	public RenderGraph Build(int width, int height)
	{
		_aspect = height > 0 ? width / (float)height : 16f / 9f;
		return RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var albedo = g.Screen.Color("albedo", Format.Rgba8Srgb);
			var normal = g.Screen.Color("normal", Format.Rgba16F);
			var position = g.Screen.Color("position", Format.Rgba16F);
			var depth = g.Screen.Depth("depth", Format.Depth32F);
			var ao = g.Screen.Color("ao", Format.R16F);
			var aoBlur = g.Screen.Color("ao_blur", Format.R16F);
			_viewproj = g.Constants<Matrix4x4>("ssao.viewproj");

			g.Pass("gbuffer")
				.Reads(_viewproj)
				.Writes(albedo, normal, position)
				.WritesDepth(depth)
				.Draw(ctx =>
				{
					DrawMesh(ctx, _cube, Matrix4x4.Identity);
					DrawMesh(ctx, _plane, Matrix4x4.Identity);
				});

			g.Pass("ssao.calculate")
				.Samples(position, normal)
				.Writes(ao)
				.Draw(ctx =>
				{
					var vp = _camera.ViewProjection;
					ctx.FullScreen("ssao", MemoryMarshal.CreateReadOnlySpan(in vp.M11, 16), position, normal);
				});

			g.Pass("ssao.blur")
				.Samples(ao)
				.Writes(aoBlur)
				.Draw(ctx => ctx.FullScreen("ssao.blur", ao));

			g.Pass("view")
				.Samples(albedo, aoBlur)
				.Writes(screen)
				.Draw(ctx => ctx.FullScreen("ssao_view", albedo, aoBlur));

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
		var call = new DrawCall { Mesh = mesh, Technique = "gbuffer" };
		call.SetUniforms(new PerDraw { Model = model, ObjectId = 0 });
		ctx.Draw(call);
	}
}
