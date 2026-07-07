using System.Numerics;

using Loom.Concepts;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.UI.SUIsei;
using SUIsei;
using SUIsei.Controls;

namespace Loom.Web.Demos;

/// <summary>
/// Group-opacity proof, hand-ported from the sandbox. The overlay is a 50%-opacity container over a canvas of two
/// overlapping translucent fills. True group opacity composites the subtree once and fades the result, so the overlap
/// stays the same colour as the non-overlap; folding the fade per child would darken the overlap. The WebGPU backend
/// proves this by rendering the faded subtree into a pooled offscreen layer and compositing it back at the group
/// alpha — the only UI path whose pixels depend on the backend, hence the cross-backend parity gate.
/// </summary>
internal sealed class UIGroupOpacityDemo : IWebDemo, IWebInteractive
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

	public UIGroupOpacityDemo(IEngine engine)
	{
		_engine = engine;
		_camera = new Camera(engine);
		var (cubeVerts, cubeIndices) = DemoGeometry.BuildCube();
		var (planeVerts, planeIndices) = DemoGeometry.BuildPlane();
		_cube = engine.CreateMesh(cubeVerts, cubeIndices);
		_plane = engine.CreateMesh(planeVerts, planeIndices);
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));

		_host = new UIHost();
		_painter = UIPainter.Create(engine, _host, FontAtlas.Default);
		var overlay = _host.CreateRoot(UILayer.Default);
		overlay.Root.Content = BuildPanel();
	}

	// A 50%-opacity container over a Canvas of two overlapping opaque fills — the same tree the sandbox builds.
	private static UIElement BuildPanel()
	{
		var overlapCanvas = new Canvas { Width = 240f, Height = 180f };
		var fillA = new Border { Width = 150f, Height = 120f, CornerRadius = 8f, Background = HdrColor.FromRgb(80, 170, 255) };
		var fillB = new Border { Width = 150f, Height = 120f, CornerRadius = 8f, Background = HdrColor.FromRgb(80, 170, 255) };
		Canvas.SetLeft(fillA, 0f);
		Canvas.SetTop(fillA, 0f);
		Canvas.SetLeft(fillB, 80f);
		Canvas.SetTop(fillB, 50f);
		overlapCanvas.Children.Add(fillA);
		overlapCanvas.Children.Add(fillB);

		return new Border
		{
			Margin = new Thickness(48f, 48f, 0f, 0f),
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			Padding = new Thickness(24f),
			Background = HdrColor.FromRgb(18, 22, 30),
			CornerRadius = 14f,
			Opacity = 0.5f,
			Child = overlapCanvas,
		};
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
			_viewproj = g.Constants<Matrix4x4>("uigroupopacity.viewproj");

			g.Pass("scene")
				.Reads(_viewproj)
				.Writes(screen)
				.WritesDepth(depth)
				.Draw(ctx =>
				{
					DrawMesh(ctx, _cube, "lit");
					DrawMesh(ctx, _plane, "lit");
				});

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
		_host.Tick(delta, time, new Vector2(_width, _height));
		_host.Render();
	}

	public void Pointer(float x, float y, int phase) { }

	private static void DrawMesh(IRenderContext ctx, Mesh mesh, string technique)
	{
		var call = new DrawCall { Mesh = mesh, Technique = technique };
		call.SetUniforms(new PerDraw { Model = Matrix4x4.Identity });
		ctx.Draw(call);
	}
}
