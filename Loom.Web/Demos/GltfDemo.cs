using System.Numerics;

using Loom.Assets;
using Loom.Concepts;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Sandbox.Demos; // the shared, linked DemoSun

namespace Loom.Web.Demos;

/// <summary>
/// glTF model loading, hand-ported from the sandbox. Loads a glTF cube (embedded in the WASM assembly, with its
/// sibling brick textures) via <see cref="GltfLoader"/> — proving the neutral asset path end to end: SharpGLTF
/// parses the file, the loader flattens it to Loom <see cref="Vertex"/> arrays (with tangents) + decoded
/// <see cref="TextureData"/>, and the host turns those into engine meshes/textures/materials. The primitives draw
/// through the normal-mapped deferred path (base-colour + tangent-space normal textures), sitting on a brick ground.
/// The first ported demo to exercise the WebGPU texture + material binding path (mipped uploads, Repeat-address
/// linear sampling, a technique's reflected multi-sampler material slots).
/// </summary>
internal sealed class GltfDemo : IWebDemo
{
	private readonly IEngine _engine;
	private readonly Camera _camera;
	private readonly Camera _sunCam;
	private readonly List<(Mesh Mesh, Material Material)> _primitives = [];
	private readonly Mesh _plane;
	private readonly Material _brick;
	private ConstantsHandle _viewproj = null!;
	private ConstantsHandle _sunViewproj = null!;
	private float _aspect = 16f / 9f;

	public GltfDemo(IEngine engine)
	{
		_engine = engine;
		_camera = new Camera(engine);
		_sunCam = DemoSun.View(engine);

		engine.DefineTechnique("depthonly", new TechniqueDescriptor("depthonly", HasVertexInput: true));
		engine.DefineTechnique("gbuffer.nm", new TechniqueDescriptor("gbuffer_normalmap", HasVertexInput: true, LinearFilter: true, Address: SamplerAddress.Repeat));
		engine.DefineTechnique("light_shadow", new TechniqueDescriptor("light_shadow", HasFragmentPush: true));

		// The brick ground material — albedo + tangent-space normal map, decoded from the embedded PNGs (also the
		// glTF's sibling textures). Matches the sandbox DemoScene's shared brick material.
		var brickAlbedo = ImageLoader.Decode(WebAssets.Load("brick_albedo.png"), TextureFormat.Rgba8Srgb);
		var brickNormal = ImageLoader.Decode(WebAssets.Load("brick_normal.png"), TextureFormat.Rgba8Unorm);
		_brick = engine.CreateMaterial(engine.CreateTexture(brickAlbedo), engine.CreateTexture(brickNormal));

		var (planeVerts, planeIndices) = DemoGeometry.BuildPlane();
		_plane = engine.CreateMesh(planeVerts, planeIndices);

		// Load the glTF cube with its sibling brick textures resolved from the embedded resources.
		var files = new Dictionary<string, byte[]>
		{
			["cube.gltf"] = WebAssets.Load("cube.gltf"),
			["brick_albedo.png"] = WebAssets.Load("brick_albedo.png"),
			["brick_normal.png"] = WebAssets.Load("brick_normal.png"),
		};
		var model = GltfLoader.LoadGltf(files, "cube.gltf");
		foreach (var prim in model.Primitives)
		{
			var mesh = engine.CreateMesh(prim.Vertices, prim.Indices);
			if (prim.Material?.BaseColor is not { } baseColor || prim.Material?.Normal is not { } normalMap)
				throw new InvalidOperationException("glTF primitive lacks a base-colour or normal texture (gbuffer.nm needs both).");
			var material = engine.CreateMaterial(engine.CreateTexture(baseColor), engine.CreateTexture(normalMap));
			_primitives.Add((mesh, material));
		}
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
			_viewproj = g.Constants<Matrix4x4>("gltf.viewproj");
			_sunViewproj = g.Constants<Matrix4x4>("gltf.sun.viewproj");

			var sun = g.Domain("sun_shadow", 1024, 1024);
			var shadowMap = sun.Depth("sun_depth", Format.Depth32F);

			g.Pass("shadow")
				.Reads(_sunViewproj)
				.WritesDepth(shadowMap)
				.Draw(ctx =>
				{
					foreach (var (mesh, _) in _primitives)
						DrawMesh(ctx, mesh, "depthonly");
					DrawMesh(ctx, _plane, "depthonly");
				});

			g.Pass("gbuffer")
				.Reads(_viewproj)
				.Writes(albedo, normal, position)
				.WritesDepth(depth)
				.Draw(ctx =>
				{
					foreach (var (mesh, material) in _primitives)
						DrawMesh(ctx, mesh, "gbuffer.nm", material);
					DrawMesh(ctx, _plane, "gbuffer.nm", _brick); // the ground, brick-textured
				});

			g.Pass("lighting")
				.Samples(albedo, normal, position, shadowMap)
				.Writes(screen)
				.Draw(ctx =>
				{
					var vp = _sunCam.ViewProjection;
					ctx.FullScreen("light_shadow", System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(in vp.M11, 16), albedo, normal, position, shadowMap);
				});

			g.Present(screen);
		});
	}

	public void Resize(int width, int height) => _aspect = height > 0 ? width / (float)height : _aspect;

	public void Update(float time)
	{
		DemoCamera.Orbit(_camera, time, _aspect);
		_engine.Write(_viewproj, _camera.ViewProjection);
		_engine.Write(_sunViewproj, _sunCam.ViewProjection);
	}

	private static void DrawMesh(IRenderContext ctx, Mesh mesh, string technique)
	{
		var call = new DrawCall { Mesh = mesh, Technique = technique };
		call.SetUniforms(new PerDraw { Model = Matrix4x4.Identity, ObjectId = 0 });
		ctx.Draw(call);
	}

	private static void DrawMesh(IRenderContext ctx, Mesh mesh, string technique, Material material)
	{
		var call = new DrawCall { Mesh = mesh, Technique = technique, Material = material };
		call.SetUniforms(new PerDraw { Model = Matrix4x4.Identity, ObjectId = 0 });
		ctx.Draw(call);
	}
}
