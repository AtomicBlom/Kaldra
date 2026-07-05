using System.Numerics;

using Loom.Concepts;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Web.Demos;

/// <summary>
/// The simplest meaningful pipeline, hand-ported from the sandbox: draw a cube and a ground plane with a single
/// forward directional light straight to the screen — one geometry pass, the shared <c>lit</c> technique, a depth
/// buffer. The clearest exercise of the WebGPU backend's mesh + uniform + depth path. The sandbox's UI/HUD overlay
/// is dropped (draw lists land in a later phase); an orbiting camera replaces the fly camera so it animates without
/// input.
/// </summary>
internal sealed class CubeLightingDemo
{
	private readonly IEngine _engine;
	private readonly Camera _camera;
	private readonly Mesh _cube;
	private readonly Mesh _plane;
	private ConstantsHandle _viewproj = null!;
	private float _aspect = 16f / 9f;

	public CubeLightingDemo(IEngine engine)
	{
		_engine = engine;
		_camera = new Camera(engine);
		var (cubeVerts, cubeIndices) = DemoGeometry.BuildCube();
		var (planeVerts, planeIndices) = DemoGeometry.BuildPlane();
		_cube = engine.CreateMesh(cubeVerts, cubeIndices);
		_plane = engine.CreateMesh(planeVerts, planeIndices);
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
	}

	public RenderGraph Build(int width, int height)
	{
		_aspect = height > 0 ? width / (float)height : 16f / 9f;
		return RenderGraph.Define(g =>
		{
			g.BackbufferClear = new Vector4(0.10f, 0.12f, 0.16f, 1.0f);
			var screen = g.Backbuffer;
			var depth = g.Screen.Depth("depth", Format.Depth32F);
			_viewproj = g.Constants<Matrix4x4>("cubelighting.viewproj");

			g.Pass("scene")
				.Reads(_viewproj)
				.Writes(screen)
				.WritesDepth(depth)
				.Draw(ctx =>
				{
					DrawMesh(ctx, _cube, Matrix4x4.Identity);
					DrawMesh(ctx, _plane, Matrix4x4.Identity);
				});
			g.Present(screen);
		});
	}

	/// <summary>Advance the orbiting camera and publish its view-projection for the scene pass to read.</summary>
	public void Update(float time)
	{
		var angle = time * 0.6f;
		var eye = new Vector3(MathF.Sin(angle) * 3.5f, 2.0f, MathF.Cos(angle) * 3.5f);
		_camera.LookAt(eye, Vector3.Zero).Perspective(MathF.PI / 4f, _aspect, 0.1f, 100f);
		_engine.Write(_viewproj, _camera.ViewProjection);
	}

	// Pack the object's model matrix into the per-draw uniform block and submit — the sandbox's DemoDraw.Mesh,
	// inlined (no material, no picking id).
	private static void DrawMesh(IRenderContext ctx, Mesh mesh, in Matrix4x4 model)
	{
		var call = new DrawCall { Mesh = mesh, Technique = "lit" };
		call.SetUniforms(new PerDraw { Model = model, ObjectId = 0 });
		ctx.Draw(call);
	}
}
