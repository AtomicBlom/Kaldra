using System.Diagnostics;
using System.Globalization;
using System.Text;

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
	private int _suspensions;
	private long _suspendedAt;

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
		// Packaged content, the same call on the desktop (a content folder beside the exe) and Android (the APK's assets).
		_page.ViewModel.Greeting = Encoding.UTF8.GetString(ReadContent("messages/greeting.txt")).Trim();
		// The count saved when the app was last suspended, so it survives the platform ending the app in the background.
		if (File.Exists(SavePath) && int.TryParse(File.ReadAllText(SavePath), out var taps))
			_page.ViewModel.RestoreTaps(taps);
		// The page is all there is, so a controller drives it: the d-pad and stick move between controls, A presses them.
		PushInputLayer(new SUIseiInputLayer(_ui) { PadNavigation = true });
		// On top, so it sees every event before the UI takes any: a readout, and Escape (Back on Android) to leave.
		PushInputLayer(new InputReadoutLayer(_page.ViewModel, Exit));

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
		// Read every frame: the scale changes when the window moves to a monitor at another scale, and the keyboard's height
		// as an on-screen keyboard opens and closes.
		_view.Root.LayoutScale = DisplayScale;
		_view.Root.InputPaneHeight = InputPaneHeight;
		_view.Root.SafeAreaInsets = SafeAreaInsets.ToThickness();
		_page.ViewModel.Keyboard = InputPaneHeight > 0f ? $"Keyboard: up, {InputPaneHeight:0} px" : "Keyboard: down";
		_ui.Tick(time.DeltaSeconds, (float)time.TotalSeconds, SurfaceSize);
		_ui.Render();
		_painter.Prepare();
	}

	// Leaving the screen may be the last the app hears before Android ends it, so the count is saved now.
	protected override void OnSuspending()
	{
		_suspensions++;
		_suspendedAt = Stopwatch.GetTimestamp();
		Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
		File.WriteAllText(SavePath, _page.ViewModel.Taps.ToString(CultureInfo.InvariantCulture));
	}

	protected override void OnResumed()
	{
		var away = Stopwatch.GetElapsedTime(_suspendedAt);
		_page.ViewModel.Lifecycle = $"Suspended {_suspensions}×, last away {away.TotalSeconds:0.0} s";
	}

	// Where the count is kept: the app's own data folder on Android, the user's local app data on the desktop.
	private static string SavePath { get; } =
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loom.HelloUI", "taps.txt");
}
