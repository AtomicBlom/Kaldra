using System.Numerics;

using Loom.Concepts;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.UI.SUIsei;
using Loom.Web.UI;
using SUIsei;
using SUIsei.Input;

namespace Loom.Web.Demos;

/// <summary>
/// The interactive, data-bound UI proof, hand-ported from the sandbox. A simple lit cube-on-plane scene renders
/// into the backbuffer; a <c>ui</c> pass then blends the composited overlay — a real <c>UIHost</c> driving the
/// <see cref="UIInteractiveView"/> XAML page (a <c>Button</c> + an <c>{x:Bind}</c> label). DOM pointer input routes
/// through the host so clicking the button updates the bound text; in capture mode a deterministic synthetic click
/// at frame 30 (over the Increment button, same coordinates as the desktop <c>--capture</c>) shows the clicked,
/// data-bound state. This exercises the WebGPU backend's <c>DrawList</c> verb — the last feature family.
/// </summary>
internal sealed class UIInteractiveDemo : IWebDemo, IWebInteractive
{
	private readonly IEngine _engine;
	private readonly Camera _camera;
	private readonly Mesh _cube;
	private readonly Mesh _plane;
	private readonly UIHost _host;
	private readonly UIPainter _painter;
	private ConstantsHandle _viewproj = null!;
	private float _aspect = 16f / 9f;
	private int _width = 1280;
	private int _height = 720;

	public UIInteractiveDemo(IEngine engine)
	{
		_engine = engine;
		_camera = new Camera(engine);
		var (cubeVerts, cubeIndices) = DemoGeometry.BuildCube();
		var (planeVerts, planeIndices) = DemoGeometry.BuildPlane();
		_cube = engine.CreateMesh(cubeVerts, cubeIndices);
		_plane = engine.CreateMesh(planeVerts, planeIndices);
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));

		// A real UI host driving the data-bound page, composited through the public draw-list API by the painter.
		_host = new UIHost();
		_painter = UIPainter.Create(engine, _host, FontAtlas.Default);
		var overlay = _host.CreateRoot(UILayer.Default);
		overlay.Root.Content = new UIInteractiveView();
	}

	public RenderGraph Build(int width, int height)
	{
		_width = width;
		_height = height;
		_aspect = height > 0 ? width / (float)height : 16f / 9f;
		return RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var depth = g.Screen.Depth("depth", Format.Depth32F);
			_viewproj = g.Constants<Matrix4x4>("uiinteractive.viewproj");

			g.Pass("scene")
				.Reads(_viewproj)
				.Writes(screen)
				.WritesDepth(depth)
				.Draw(ctx =>
				{
					DrawMesh(ctx, _cube, "lit");
					DrawMesh(ctx, _plane, "lit");
				});

			// The composited overlay, blended over the scene at native resolution.
			g.Pass("ui").Blends(screen).Draw(ctx => _painter.Draw(ctx, UISurface.Hud));
			g.Present(screen);
		});
	}

	public void Resize(int width, int height)
	{
		_width = width;
		_height = height;
		_aspect = height > 0 ? width / (float)height : _aspect;
	}

	public void Update(float time)
	{
		DemoCamera.Orbit(_camera, time, _aspect);
		_engine.Write(_viewproj, _camera.ViewProjection);
	}

	public void Advance(float delta, float time, int frame, bool capturing)
	{
		// Deterministic capture: inject one press+release over the page's Increment button so the captured frame shows
		// the clicked/data-bound state — the same coordinates the desktop --capture protocol uses.
		if (capturing && frame == 30)
		{
			var click = new Vector2(144f, 68f);
			_host.QueuePointer(PointerInput.Press(click));
			_host.QueuePointer(PointerInput.Release(click));
		}

		_host.Tick(delta, time, new Vector2(_width, _height));
		_host.Render();
	}

	public void Pointer(float x, float y, int phase)
	{
		var p = new Vector2(x, y);
		_host.QueuePointer(phase switch
		{
			1 => PointerInput.Press(p),
			2 => PointerInput.Release(p),
			_ => PointerInput.Move(p),
		});
	}

	private static void DrawMesh(IRenderContext ctx, Mesh mesh, string technique)
	{
		var call = new DrawCall { Mesh = mesh, Technique = technique };
		call.SetUniforms(new PerDraw { Model = Matrix4x4.Identity });
		ctx.Draw(call);
	}
}
