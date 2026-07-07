using System.Numerics;
using System.Runtime.InteropServices;

using Loom.Concepts;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Sandbox; // the shared, linked Cascades fitting

namespace Loom.Web.Demos;

/// <summary>
/// Cascaded shadow maps, hand-ported from the sandbox. A cube on a large ground, lit by the sun, shadowed by three
/// cascades fitted to the camera frustum (near→far) so the shadow stays crisp across distance where a single map
/// would smear. The deferred g-buffer feeds a <c>lighting_csm</c> pass that selects a cascade per pixel by view
/// depth and samples that cascade's map; the cascade matrices/splits ride the per-frame uniform seam. Exercises the
/// WebGPU backend's depth-only passes (three 1024² shadow maps), a multi-input lighting pass sampling six textures
/// plus two uniform blocks, and — the reason this is a parity gate — the large (272-byte) per-draw constant buffer
/// carrying the three cascade matrices, which rides the per-draw-uniform channel rather than the push channel.
/// </summary>
internal sealed class CascadedShadowsDemo : IWebDemo
{
	// The cascade split far-depths (.xyz) + a cascade-tint debug flag (.w) — a single float4 row, the same layout
	// the shared lighting_csm shader reads at uniform slot 0.
	private readonly record struct CsmSplits(Vector4 P);

	private const int CascadeCount = 3;
	private const float ShadowResolution = 1024f;
	private const float CamNear = 0.5f;
	private const float CamFar = 60f;
	private static readonly Vector3 SunDir = Vector3.Normalize(new Vector3(0.5f, 0.7f, -0.5f));

	private readonly IEngine _engine;
	private readonly Camera _cam;
	private readonly Camera[] _cascadeCams = new Camera[CascadeCount];
	private readonly float[] _splits = new float[CascadeCount];
	private readonly float[] _csmParams = new float[68]; // 3×mat4 (48) + eye + forward + sunDir + sunColor + ambient
	private readonly Mesh _cube;
	private readonly Mesh _ground;
	private ConstantsHandle _csmSplits = null!;
	private ConstantsHandle _viewproj = null!;
	private readonly ConstantsHandle[] _cascadeViewproj = new ConstantsHandle[CascadeCount];
	private float _aspect = 16f / 9f;

	public CascadedShadowsDemo(IEngine engine)
	{
		_engine = engine;
		_cam = new Camera(engine);
		for (var c = 0; c < CascadeCount; c++)
			_cascadeCams[c] = new Camera(engine);

		var (cubeVerts, cubeIndices) = DemoGeometry.BuildCube();
		_cube = engine.CreateMesh(cubeVerts, cubeIndices);

		// A large flat ground (±40 on X/Z at y=-0.5, so the unit cube sits on it) — wide + deep enough that the view
		// spans all three cascades.
		const float s = 40f, y = -0.5f;
		var up = Vector3.UnitY;
		var tan = new Vector4(1, 0, 0, 1);
		Vertex[] groundVerts =
		[
			new(new Vector3(-s, y, -s), up, new Vector2(0, 0), tan),
			new(new Vector3(s, y, -s), up, new Vector2(1, 0), tan),
			new(new Vector3(s, y, s), up, new Vector2(1, 1), tan),
			new(new Vector3(-s, y, s), up, new Vector2(0, 1), tan),
		];
		_ground = engine.CreateMesh(groundVerts, [0, 1, 2, 0, 2, 3]);

		engine.DefineTechnique("gbuffer", new TechniqueDescriptor("gbuffer", HasVertexInput: true));
		engine.DefineTechnique("depthonly", new TechniqueDescriptor("depthonly", HasVertexInput: true));
		engine.DefineTechnique("lighting_csm", new TechniqueDescriptor("lighting_csm"));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
	}

	public RenderGraph Build(int width, int height)
	{
		_aspect = height > 0 ? width / (float)height : 16f / 9f;
		return RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			_csmSplits = g.Constants<CsmSplits>("csm.splits");
			_viewproj = g.Constants<Matrix4x4>("csm.viewproj");
			var albedo = g.Screen.Color("albedo", Format.Rgba8Srgb);
			var normal = g.Screen.Color("normal", Format.Rgba16F);
			var position = g.Screen.Color("position", Format.Rgba16F);
			var depth = g.Screen.Depth("depth", Format.Depth32F);
			var lit = g.Screen.Color("lit", Format.Rgba16F);

			// One 1024² depth domain per cascade.
			var cascadeMaps = new DepthHandle[CascadeCount];
			for (var c = 0; c < CascadeCount; c++)
			{
				var dom = g.Domain($"sun_csm{c}", (int)ShadowResolution, (int)ShadowResolution);
				cascadeMaps[c] = dom.Depth($"csm_depth{c}", Format.Depth32F);
				_cascadeViewproj[c] = g.Constants<Matrix4x4>($"csm.cascade{c}.viewproj");
			}

			// A depth-only pass per cascade, drawing the scene from that cascade's sun view.
			for (var c = 0; c < CascadeCount; c++)
			{
				var cascade = c; // capture by value
				g.Pass($"shadow{c}")
					.Reads(_cascadeViewproj[cascade])
					.WritesDepth(cascadeMaps[cascade])
					.Draw(ctx =>
					{
						DrawMesh(ctx, _cube, "depthonly");
						DrawMesh(ctx, _ground, "depthonly");
					});
			}

			g.Pass("gbuffer")
				.Reads(_viewproj)
				.Writes(albedo, normal, position)
				.WritesDepth(depth)
				.Draw(ctx =>
				{
					DrawMesh(ctx, _cube, "gbuffer");
					DrawMesh(ctx, _ground, "gbuffer");
				});

			g.Pass("lighting")
				.Samples(albedo, normal, position, cascadeMaps[0], cascadeMaps[1], cascadeMaps[2])
				.Reads(_csmSplits)
				.Writes(lit)
				.Draw(ctx => ctx.FullScreen("lighting_csm", PackParams(), albedo, normal, position, cascadeMaps[0], cascadeMaps[1], cascadeMaps[2]));

			g.Pass("blit").Samples(lit).Writes(screen).Draw(ctx => ctx.FullScreen("blit", lit));
			g.Present(screen);
		});
	}

	public void Resize(int width, int height) => _aspect = height > 0 ? width / (float)height : _aspect;

	public void Update(float time)
	{
		// A low camera looking across the ground into the distance, so near→far spans the cascades.
		_cam.Perspective(MathF.PI / 3f, _aspect, CamNear, CamFar).LookAt(new Vector3(0f, 4f, 16f), new Vector3(0f, 0f, -8f));

		Cascades.Compute(_cam, SunDir, CamNear, CamFar, ShadowResolution, _cascadeCams, _splits);
		_engine.Write(_csmSplits, new CsmSplits(new Vector4(_splits[0], _splits[1], _splits[2], 0f)));
		_engine.Write(_viewproj, _cam.ViewProjection);
		for (var c = 0; c < CascadeCount; c++)
			_engine.Write(_cascadeViewproj[c], _cascadeCams[c].ViewProjection);
	}

	// Pack the per-frame CSM uniform block: three cascade view-projections, then the camera eye + forward (for the
	// view-depth metric), and the sun direction + key intensity + colours. The splits + tint flag ride CsmSplits.
	private ReadOnlySpan<float> PackParams()
	{
		var p = _csmParams;
		for (var c = 0; c < CascadeCount; c++)
		{
			var m = _cascadeCams[c].ViewProjection;
			MemoryMarshal.CreateReadOnlySpan(in m.M11, 16).CopyTo(p.AsSpan(c * 16, 16));
		}
		Matrix4x4.Invert(_cam.View, out var invView);
		var eye = invView.Translation;
		var forward = -Vector3.Normalize(new Vector3(invView.M31, invView.M32, invView.M33));
		p[48] = eye.X; p[49] = eye.Y; p[50] = eye.Z; p[51] = 0f;
		p[52] = forward.X; p[53] = forward.Y; p[54] = forward.Z; p[55] = 0f;
		p[56] = SunDir.X; p[57] = SunDir.Y; p[58] = SunDir.Z; p[59] = 1.8f; // sun dir + key intensity
		p[60] = 1f; p[61] = 1f; p[62] = 1f; p[63] = 0f;                     // white sun light
		p[64] = 0.15f; p[65] = 0.15f; p[66] = 0.15f; p[67] = 0f;            // flat grey ambient
		return p;
	}

	private static void DrawMesh(IRenderContext ctx, Mesh mesh, string technique)
	{
		var call = new DrawCall { Mesh = mesh, Technique = technique };
		call.SetUniforms(new PerDraw { Model = Matrix4x4.Identity, ObjectId = 0 });
		ctx.Draw(call);
	}
}
