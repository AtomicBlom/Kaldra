using HelloSynth.Demo;

using Loom.Audio;
using Loom.Audio.Workbench;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.UI.SUIsei;

using SUIsei;

namespace HelloSynth;

/// <summary>
/// The music workbench over the demo catalog, over a backdrop. It keeps playing while the editor has the focus, and saves
/// what it was doing, so a restart under dotnet watch comes back playing where it was.
/// </summary>
public sealed class HelloSynthApp : LoomApplication
{
	private UIHost _ui = null!;
	private UIPainter _painter = null!;
	private UIView _view = null!;
	private Workbench _workbench = null!;
	private double _sinceSave;

	protected override void Configure(AppOptions options)
	{
		options.Title = "Loom - Hello Synth";
		options.Width = 1280;
		options.Height = 800;
		// The default unfocused volume (1): the music plays on while the editor has the focus
		options.Audio = new AudioOptions();
	}

	protected override void LoadContent()
	{
		Engine.DefineTechnique("background", new TechniqueDescriptor("background"));
		_ui = new UIHost();
		_painter = UIPainter.Create(Engine, _ui);
		_view = _ui.CreateRoot(UILayer.Default);

		var music = Audio.CreateBus("music");
		_workbench = new Workbench(Audio, music, DemoCatalog.Make());
		if (State.Load() is { } state)
			_workbench.RestoreState(state);
		_view.Root.Content = new WorkbenchView(_workbench.ViewModel);
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

	protected override void Update(FrameTime time)
	{
		_workbench.HandleInput(Input);
		_workbench.Update(time.DeltaSeconds);
		// What it is doing saved a second after it changes, so a restart finds it
		_sinceSave += time.DeltaSeconds;
		if (_sinceSave >= 1.0)
		{
			_sinceSave = 0;
			State.Save(_workbench.CaptureState());
		}
	}

	protected override void BeforeRender(FrameTime time)
	{
		_view.Root.LayoutScale = WorkbenchView.FitScale(SurfaceSize, DisplayScale);
		_ui.Tick(time.DeltaSeconds, (float)time.TotalSeconds, SurfaceSize);
		_ui.Render();
		_painter.Prepare();
	}

	protected override void UnloadContent()
	{
		State.Save(_workbench.CaptureState());
		_workbench.Dispose();
	}

	private static readonly WorkbenchStateFile State =
		new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loom.HelloSynth", "workbench.json"));
}
