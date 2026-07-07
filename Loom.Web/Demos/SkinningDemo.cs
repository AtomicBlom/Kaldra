using System.Numerics;

using Loom.Assets;
using Loom.Concepts;
using Loom.Core.Animation;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Web.Demos;

/// <summary>
/// Skeletal animation via GPU compute pre-skin, hand-ported from the sandbox. Loads a rigged glTF (a 2-bone bar with
/// a "bend" clip) embedded in the WASM assembly, then each frame: the CPU <see cref="AnimationSampler"/> samples the
/// clip at <c>ctx.Time</c> into a bone palette, a compute pass skins the bind-pose vertices once into a transient
/// buffer (<c>ctx.DispatchSkin</c>), and the scene pass draws that skinned output with the ordinary forward
/// <c>lit</c> technique (<c>ctx.DrawSkinned</c>). Exercises the WebGPU backend's compute pre-skin path — three
/// storage buffers (bind-pose, per-frame palette ring, skinned output) and the compute→vertex hand-off the
/// <c>.ReadWrites</c> → <c>.ReadsVertex</c> edges derive.
/// </summary>
internal sealed class SkinningDemo : IWebDemo
{
	private readonly IEngine _engine;
	private readonly Camera _camera;
	private readonly Mesh _mesh;
	private readonly Mesh _plane;
	private readonly int _vertexCount;
	private readonly Skeleton _skeleton;
	private readonly AnimationClip _clip;
	private readonly Matrix4x4[] _palette;
	private ConstantsHandle _viewproj = null!;
	private float _aspect = 16f / 9f;

	public SkinningDemo(IEngine engine)
	{
		_engine = engine;
		_camera = new Camera(engine);

		engine.DefineTechnique("skin", new TechniqueDescriptor("skin", VertexEntry: "main", FragmentEntry: "main", IsCompute: true, HasFragmentPush: true));
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));

		var model = GltfLoader.LoadGlb(WebAssets.Load("rigged_bar.glb"));
		if (model.Skeleton is null || model.Clips.Count == 0)
			throw new InvalidOperationException("'rigged_bar.glb' is not rigged (no skeleton/clips).");
		var prim = model.Primitives[0];
		_mesh = engine.CreateSkinnedMesh(prim.SkinnedVertices!, prim.Indices);
		_vertexCount = prim.SkinnedVertices!.Length;
		_skeleton = model.Skeleton;
		_clip = model.Clips[0];
		_palette = new Matrix4x4[_skeleton.JointCount];

		var (planeVerts, planeIndices) = DemoGeometry.BuildPlane();
		_plane = engine.CreateMesh(planeVerts, planeIndices);
	}

	public RenderGraph Build(int width, int height)
	{
		_aspect = height > 0 ? width / (float)height : 16f / 9f;
		return RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var lit = g.Screen.Color("lit", Format.Rgba16F);
			var depth = g.Screen.Depth("depth", Format.Depth32F);
			var skinned = g.Buffer("skinned", _vertexCount * 48); // MeshStandard-shaped (48 bytes) compute output
			_viewproj = g.Constants<Matrix4x4>("skinning.viewproj");

			// Pre-skin the bar once: sample the clip at this frame's time (looping) into the palette, then dispatch.
			g.Compute("skin")
				.ReadWrites(skinned)
				.Dispatch(ctx =>
				{
					var t = _clip.Duration > 0f ? ctx.Time % _clip.Duration : 0f;
					AnimationSampler.Evaluate(_skeleton, _clip, t, _palette);
					ctx.DispatchSkin("skin", _mesh, _palette, skinned, _vertexCount);
				});

			// Draw the skinned output (forward lit) + the ground plane; the .ReadsVertex edge derives the barrier.
			g.Pass("scene")
				.Reads(_viewproj)
				.Writes(lit)
				.WritesDepth(depth)
				.ReadsVertex(skinned)
				.Draw(ctx =>
				{
					var skinnedCall = new DrawCall { Mesh = _mesh, Technique = "lit", SkinnedVertices = skinned };
					skinnedCall.SetUniforms(new PerDraw { Model = Matrix4x4.Identity, ObjectId = 0 });
					ctx.Draw(skinnedCall);
					DrawMesh(ctx, _plane, Matrix4x4.Identity);
				});

			g.Pass("blit").Samples(lit).Writes(screen).Draw(ctx => ctx.FullScreen("blit", lit));
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
