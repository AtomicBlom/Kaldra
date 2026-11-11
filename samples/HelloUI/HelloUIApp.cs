using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.UI.SUIsei;

using SUIsei;

namespace HelloUI;

/// <summary>
/// A SUIsei page over a backdrop, the same source on the desktop and on Android. The page lays out in layout units
/// scaled by <see cref="LoomApplication.DisplayScale"/>, so it keeps one physical size on a 100% monitor, a 200% tablet
/// and a phone, and it takes clicks, taps and drags through the same input layer on all of them.
/// </summary>
public sealed class HelloUIApp : LoomApplication
{
	private UIHost _ui = null!;
	private UIPainter _painter = null!;
	private UIView _view = null!;
	private MainPage _page = null!;

	protected override void Configure(AppOptions options)
	{
		options.Title = "Loom - Hello UI";
		options.Width = 1280;
		options.Height = 800;
	}

	protected override void LoadContent()
	{
		Engine.DefineTechnique("background", new TechniqueDescriptor("background"));

		_ui = new UIHost();
		_painter = UIPainter.Create(Engine, _ui);
		_view = _ui.CreateRoot(UILayer.Default);
		_page = new MainPage();
		_page.ViewModel.Device = $"{Engine.DeviceName} ({Engine.BackendName})";
		_view.Root.Content = _page;
		PushInputLayer(new SUIseiInputLayer(_ui));

		SetGraph(RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			g.Pass("background")
				.Writes(screen)
				.Draw(static ctx => ctx.FullScreen("background"));
			g.Pass("ui")
				.Blends(screen)
				.Draw((_painter, _view.Surface), static (ctx, ui) => ui.Item1.Draw(ctx, ui.Item2));
			g.Present(screen);
		}));
	}

	protected override void BeforeRender(FrameTime time)
	{
		// Read every frame: it changes when the window moves to a monitor at another scale.
		_view.Root.LayoutScale = DisplayScale;
		_ui.Tick(time.DeltaSeconds, (float)time.TotalSeconds, SurfaceSize);
		_ui.Render();
		_painter.Prepare();
	}
}
