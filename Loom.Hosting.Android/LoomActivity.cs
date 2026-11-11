using Android.App;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;

using Loom.Backend.Vulkan;
using Loom.Core.Hosting;

namespace Loom.Hosting.Android;

/// <summary>
/// The activity a Loom app runs in on Android. Subclass it, return the application from
/// <see cref="CreateApplication"/>, and mark the subclass as the launcher activity, handling the configuration changes
/// in <see cref="HandledConfigChanges"/> itself:
/// <code>
/// [Activity(MainLauncher = true, ConfigurationChanges = LoomActivity.HandledConfigChanges, FullscreenTheme = LoomActivity.FullscreenTheme)]
/// public sealed class MainActivity : LoomActivity
/// {
///     protected override LoomApplication CreateApplication() =&gt; new MyApp();
/// }
/// </code>
/// The activity fills its window with a <see cref="SurfaceView"/>, brings the Vulkan engine up on its surface, and runs
/// one frame per display refresh from the <see cref="Choreographer"/>, on the UI thread, where Android delivers touch
/// too, so input reaches the app on the thread its frames run on. A failure shows its message in place of the frame
/// and goes to logcat under the tag <c>Loom</c>.
/// </summary>
/// <remarks>
/// The engine lives as long as the activity's surface. Android takes the surface away when the app leaves the screen,
/// and the engine cannot yet move to a new one, so the activity unloads the app, disposes the engine and finishes then:
/// leaving the app closes it. Rotation and resizing keep the surface (the activity handles those configuration changes
/// itself) and reach the app as a resize.
/// </remarks>
public abstract class LoomActivity : Activity, ISurfaceHolderCallback, Choreographer.IFrameCallback
{
	/// <summary>
	/// The configuration changes a <see cref="LoomActivity"/> handles itself rather than being destroyed and made again,
	/// which would take its surface and so its engine with it. Pass it as the <c>ConfigurationChanges</c> of the
	/// subclass's <c>[Activity]</c>.
	/// </summary>
	public const ConfigChanges HandledConfigChanges =
		ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout
		| ConfigChanges.Density | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation
		| ConfigChanges.UiMode | ConfigChanges.Locale | ConfigChanges.LayoutDirection;

	/// <summary>A theme with no title bar or action bar, for the subclass's <c>[Activity]</c>: the frame fills the window.</summary>
	public const string FullscreenTheme = "@android:style/Theme.Material.NoActionBar.Fullscreen";

	private const string LogTag = "Loom";

	private IHostedApplication? _app;
	private SurfaceView? _view;
	private Choreographer? _choreographer;
	private AndroidInputPump? _input;
	private IEngine? _engine;
	private nint _window;
	private bool _resumed;
	private bool _framesRunning;
	private bool _failed;
	private long _lastFrameNanos;
	private double _totalSeconds;

	/// <summary>Physical pixels per layout unit on the screen the activity is on: Android's display density.</summary>
	internal float CurrentDisplayScale { get; private set; } = 1f;

	/// <summary>The application this activity runs. Called once, from <c>OnCreate</c>.</summary>
	protected abstract LoomApplication CreateApplication();

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
		EnterFullscreen();
		CurrentDisplayScale = Resources?.DisplayMetrics?.Density ?? 1f;
		_choreographer = Choreographer.Instance;

		var application = CreateApplication();
		LoomApplication.Run(application, []);
		_app = application;

		_view = new SurfaceView(this);
		_view.Holder!.AddCallback(this);
		SetContentView(_view);
	}

	protected override void OnResume()
	{
		base.OnResume();
		_resumed = true;
		StartFrames();
	}

	protected override void OnPause()
	{
		_resumed = false;
		StopFrames();
		base.OnPause();
	}

	protected override void OnDestroy()
	{
		ShutDownEngine();
		base.OnDestroy();
	}

	public override void OnConfigurationChanged(Configuration newConfig)
	{
		base.OnConfigurationChanged(newConfig);
		if (newConfig.DensityDpi > 0)
			CurrentDisplayScale = newConfig.DensityDpi / (float)DisplayMetricsDensity.Default;
	}

	public override void OnWindowFocusChanged(bool hasFocus)
	{
		base.OnWindowFocusChanged(hasFocus);
		if (!hasFocus)
			_input?.ReleasePrimary(); // a press held as the window loses focus never gets its up
		_app?.SetFocus(hasFocus);
	}

	public override bool OnTouchEvent(MotionEvent? e) =>
		(e is not null && _input is not null && _input.OnTouch(e)) || base.OnTouchEvent(e);

	public override bool OnGenericMotionEvent(MotionEvent? e) =>
		(e is not null && _input is not null && _input.OnGenericMotion(e)) || base.OnGenericMotionEvent(e);

	void ISurfaceHolderCallback.SurfaceCreated(ISurfaceHolder holder)
	{
		// The size arrives with SurfaceChanged, which Android always calls next; the engine comes up there.
	}

	void ISurfaceHolderCallback.SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height)
	{
		if (_failed || width <= 0 || height <= 0)
			return;
		if (_engine is null)
		{
			BringUpEngine(holder, width, height);
			return;
		}
		try
		{
			_engine.Resize(width, height);
			_app!.Resize(width, height);
		}
		catch (Exception ex)
		{
			Fail("resizing the engine", ex);
		}
	}

	void ISurfaceHolderCallback.SurfaceDestroyed(ISurfaceHolder holder)
	{
		// The surface goes once this returns, so the engine must be done with it first.
		ShutDownEngine();
		if (!IsFinishing)
			Finish();
	}

	void Choreographer.IFrameCallback.DoFrame(long frameTimeNanos)
	{
		if (!_framesRunning || _engine is null)
			return;

		var delta = _lastFrameNanos == 0 ? 1.0 / 60.0 : (frameTimeNanos - _lastFrameNanos) / 1_000_000_000.0;
		_lastFrameNanos = frameTimeNanos;
		_totalSeconds += delta;
		try
		{
			_app!.Tick(new FrameTime(delta, _totalSeconds));
		}
		catch (Exception ex)
		{
			Fail("running a frame", ex);
			return;
		}
		_choreographer!.PostFrameCallback(this);
	}

	private void BringUpEngine(ISurfaceHolder holder, int width, int height)
	{
		try
		{
			var app = _app!;
			app.SetArgs([]); // no command line on Android
			var options = app.ReadOptions();

			// The app's shaders, and its UI library's, are embedded in their assemblies: an APK has no shader folder.
			var settings = new VulkanEngineSettings { ShaderResolver = EmbeddedShaders.Resolve };
			options.EngineSettings.CopyBaseSettingsTo(settings);
			settings.PreferredBackend = "Vulkan";

			_window = NativeWindow.FromSurface(holder.Surface!);
			_engine = new VulkanBackend().Create(new AndroidVkSurface(_window), width, height, settings);
			Log.Info(LogTag, $"Loom running on {_engine.DeviceName} ({_engine.BackendName}) at {width}x{height}, display scale {CurrentDisplayScale:0.##}");

			_input = new AndroidInputPump(app);
			app.Load(_engine, new AndroidHostControl(this));
			// The host reports the initial size before the first frame, exactly like every later resize.
			app.Resize(width, height);
			StartFrames();
		}
		catch (Exception ex)
		{
			Fail("bringing the engine up", ex);
		}
	}

	private void ShutDownEngine()
	{
		StopFrames();
		if (_engine is { } engine)
		{
			_engine = null;
			try
			{
				_app?.Unload();
				engine.WaitIdle();
				engine.Dispose();
			}
			catch (Exception ex)
			{
				Log.Error(LogTag, $"Shutting the engine down failed: {ex}");
			}
		}
		NativeWindow.Release(_window);
		_window = 0;
		_input = null;
	}

	private void StartFrames()
	{
		if (_framesRunning || !_resumed || _engine is null || _failed)
			return;
		_framesRunning = true;
		_lastFrameNanos = 0; // the time paused is not a frame's delta
		_choreographer!.PostFrameCallback(this);
	}

	private void StopFrames()
	{
		if (!_framesRunning)
			return;
		_framesRunning = false;
		_choreographer!.RemoveFrameCallback(this);
	}

	// Stop the frames and put what went wrong on the screen, where a device with no debugger attached still shows it.
	private void Fail(string doing, Exception ex)
	{
		_failed = true;
		StopFrames();
		var message = $"Loom failed {doing}:\n\n{ex}";
		Log.Error(LogTag, message);
		var text = new TextView(this) { Text = message, TextSize = 12f };
		text.SetTextColor(Color.White);
		text.SetBackgroundColor(Color.Rgb(40, 10, 10));
		text.SetPadding(32, 48, 32, 48);
		var scroll = new ScrollView(this);
		scroll.AddView(text);
		// The surface view stays in the window, beneath, so its surface is not taken away from an engine still holding it.
		AddContentView(scroll, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
	}

	// Draw under the system bars and the display cutout, hide the bars until swiped in, and keep the screen on.
	private void EnterFullscreen()
	{
		var window = Window!;
		window.AddFlags(WindowManagerFlags.KeepScreenOn);
		var attributes = window.Attributes!;
		attributes.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
		window.Attributes = attributes;
		// Android 15 draws every app edge to edge already, and obsoletes the switch.
		if (OperatingSystem.IsAndroidVersionAtLeast(30) && !OperatingSystem.IsAndroidVersionAtLeast(35))
			window.SetDecorFitsSystemWindows(false);
		if (OperatingSystem.IsAndroidVersionAtLeast(30) && window.InsetsController is { } insets)
		{
			insets.Hide(WindowInsets.Type.SystemBars());
			insets.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
		}
	}
}
