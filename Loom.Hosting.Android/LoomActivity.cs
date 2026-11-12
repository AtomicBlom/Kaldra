using Android.App;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.Hardware.Input;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Android.Window;

using Loom.Backend.Vulkan;
using Loom.Core.Hosting;
using Loom.Input;

namespace Loom.Hosting.Android;

/// <summary>
/// The activity a Loom app runs in on Android. Subclass it, return the application from
/// <see cref="CreateApplication"/>, and mark the subclass as the launcher activity, handling the configuration changes
/// in <see cref="HandledConfigChanges"/> itself:
/// <code>
/// [Activity(MainLauncher = true, ConfigurationChanges = LoomActivity.HandledConfigChanges, Theme = LoomActivity.FullscreenTheme)]
/// public sealed class MainActivity : LoomActivity
/// {
///     protected override LoomApplication CreateApplication() =&gt; new MyApp();
/// }
/// </code>
/// The activity fills its window with a <see cref="SurfaceView"/>, brings the Vulkan engine up on its surface, and runs
/// one frame per display refresh from the <see cref="Choreographer"/>, on the UI thread, where Android delivers input
/// too, so input reaches the app on the thread its frames run on. A failure shows its message in place of the frame
/// and goes to logcat under the tag <c>Loom</c>.
/// </summary>
/// <remarks>
/// <para>
/// The engine and the app live as long as the activity. Android takes the surface away whenever the app leaves the
/// screen; the activity detaches the engine from it (<see cref="IReattachableSurface"/>) and stops the frames, and when
/// the app comes back it attaches the new surface and carries on, with nothing reloaded. Rotation and resizing keep the
/// surface (the activity handles those configuration changes itself) and reach the app as a resize.
/// </para>
/// <para>
/// Input: touch, a pen and a mouse drive the pointer; a keyboard sends keys and text; game controllers are pads
/// (rumble included). The system's Back gesture or button reaches the app as <see cref="Key.Escape"/> pressed and
/// released, and leaves the app only if the app then calls <c>Exit</c>, so a game can take Back for its own menus. The
/// on-screen keyboard opens while the app wants text (a text field has focus) and closes when it stops; its height
/// reaches the app as <c>LoomApplication.InputPaneHeight</c>, for laying the page out clear of it.
/// </para>
/// </remarks>
public abstract class LoomActivity : Activity, ISurfaceHolderCallback, Choreographer.IFrameCallback, View.IOnTouchListener,
	View.IOnGenericMotionListener, View.IOnApplyWindowInsetsListener
{
	/// <summary>
	/// The configuration changes a <see cref="LoomActivity"/> handles itself rather than being destroyed and made again,
	/// which would take its surface and so its engine with it: the display (orientation, size, density, colour mode),
	/// input devices, and the user's text and locale settings. Pass it as the <c>ConfigurationChanges</c> of the
	/// subclass's <c>[Activity]</c>.
	/// </summary>
#pragma warning disable CA1416 // FontWeightAdjustment is Android 12's; this is a manifest flag set, and an older Android ignores a bit it does not know
	public const ConfigChanges HandledConfigChanges =
		ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout
		| ConfigChanges.Density | ConfigChanges.ColorMode | ConfigChanges.UiMode
		| ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation | ConfigChanges.Touchscreen
		| ConfigChanges.Locale | ConfigChanges.LayoutDirection | ConfigChanges.FontScale | ConfigChanges.FontWeightAdjustment
		| ConfigChanges.Mcc | ConfigChanges.Mnc;
#pragma warning restore CA1416

	/// <summary>A theme with no title bar or action bar, for the subclass's <c>[Activity]</c>: the frame fills the window.</summary>
	public const string FullscreenTheme = "@android:style/Theme.Material.NoActionBar.Fullscreen";

	private const string LogTag = "Loom";

	private IHostedApplication? _app;
	private LoomSurfaceView? _view;
	private Choreographer? _choreographer;
	private AndroidInputPump? _input;
	private AndroidGamepads? _gamepads;
	private BackCallback? _backCallback;
	private IEngine? _engine;
	private nint _window;
	private bool _resumed;
	private bool _framesRunning;
	private bool _failed;
	// Set once OnDestroy has run. Android can still call the surface callbacks after it (a relaunch detaches the old
	// activity's view after destroying it), and those must not act: Finish() from the old instance would finish the new.
	private bool _destroyed;
	private long _lastFrameNanos;
	private double _totalSeconds;

	// The on-screen keyboard: whether the activity asked for it to be up, whether it is up (from the window insets), and
	// whether a tap since the last frame should bring it back after the user put it away.
	private bool _imeRequested;
	private bool _imeVisible;
	private int _imeHeight;
	private ScreenInsets _safeArea;
	private bool _imeReshow;

	/// <summary>Physical pixels per layout unit on the screen the activity is on: Android's display density.</summary>
	internal float CurrentDisplayScale { get; private set; } = 1f;

	/// <summary>The application this activity runs. Called once, from <c>OnCreate</c>.</summary>
	protected abstract LoomApplication CreateApplication();

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
		CurrentDisplayScale = Resources?.DisplayMetrics?.Density ?? 1f;
		_choreographer = Choreographer.Instance;

		var application = CreateApplication();
		LoomApplication.Run(application, []);
		_app = application;
		_input = new AndroidInputPump(application);
		_gamepads = new AndroidGamepads(application, GetSystemService(InputService) as InputManager);

		_view = new LoomSurfaceView(this, _input);
		_view.Holder!.AddCallback(this);
		// Input from the view itself, so positions are relative to the surface the frame is drawn on, wherever the system
		// bars leave it in the window.
		_view.SetOnTouchListener(this);
		_view.SetOnGenericMotionListener(this);
		_view.SetOnApplyWindowInsetsListener(this);
		SetContentView(_view);
		_view.RequestFocus();
		// After the content view: the window's insets controller belongs to its decor view, which setting content creates.
		EnterFullscreen();

		if (OperatingSystem.IsAndroidVersionAtLeast(33))
		{
			_backCallback = new BackCallback(this);
			OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(IOnBackInvokedDispatcher.PriorityDefault, _backCallback);
		}
	}

	protected override void OnResume()
	{
		base.OnResume();
		_resumed = true;
		_app?.Resume(); // a no-op unless the app was suspended
		StartFrames();
	}

	protected override void OnPause()
	{
		_resumed = false;
		StopFrames();
		// Android may end the process any time after this with no further call, so the app saves now. An app not loaded yet
		// (left during start-up) has nothing to save and is not told.
		if (_engine is not null)
		{
			try
			{
				_app?.Suspend();
			}
			catch (Exception ex)
			{
				Log.Error(LogTag, $"The app failed while suspending: {ex}");
			}
		}
		base.OnPause();
	}

	protected override void OnDestroy()
	{
		_destroyed = true;
		_view?.Holder?.RemoveCallback(this);
		if (_backCallback is not null && OperatingSystem.IsAndroidVersionAtLeast(33))
			OnBackInvokedDispatcher.UnregisterOnBackInvokedCallback(_backCallback);
		_gamepads?.Stop();
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
		{
			// What is held as the window loses focus never gets its release.
			var timestamp = SystemClock.UptimeMillis();
			_input?.ReleaseAll(timestamp);
			_gamepads?.ReleaseAll(timestamp);
		}
		_app?.SetFocus(hasFocus);
	}

	public override bool DispatchKeyEvent(KeyEvent? e)
	{
		if (e is null || _input is null)
			return base.DispatchKeyEvent(e);
		if (e.KeyCode == Keycode.Back)
		{
			// Below Android 13, Back arrives as a key; from it, as the callback registered in OnCreate.
			if (e.Action == KeyEventActions.Up && e.RepeatCount == 0 && !e.IsCanceled)
				OnBack();
			return true;
		}
		if (_gamepads?.OnKey(e) == true || _input.OnKey(e))
			return true;
		return base.DispatchKeyEvent(e);
	}

	public override bool DispatchGenericMotionEvent(MotionEvent? e) =>
		(e is not null && _gamepads?.OnMotion(e) == true) || base.DispatchGenericMotionEvent(e);

	bool View.IOnTouchListener.OnTouch(View? view, MotionEvent? e)
	{
		if (e is null || _input is null)
			return false;
		if (e.ActionMasked == MotionEventActions.Down)
			_imeReshow = true; // a tap brings the keyboard back if a text field still wants it
		return _input.OnTouch(e);
	}

	bool View.IOnGenericMotionListener.OnGenericMotion(View? view, MotionEvent? e) =>
		e is not null && _input is not null && _input.OnGenericMotion(e);

	WindowInsets View.IOnApplyWindowInsetsListener.OnApplyWindowInsets(View view, WindowInsets insets)
	{
		// The keyboard covers the bottom of the window, and the surface fills the window, so its inset is the part of the
		// surface it covers. Android 10 reports no keyboard inset; the app sees none there.
		if (OperatingSystem.IsAndroidVersionAtLeast(30))
		{
			var ime = WindowInsets.Type.Ime();
			var visible = insets.IsVisible(ime);
			if (!visible && _imeVisible)
				_view?.Connection?.Reset();
			var height = visible ? insets.GetInsets(ime).Bottom : 0;
			if (visible != _imeVisible || height != _imeHeight)
				Log.Info(LogTag, visible ? $"on-screen keyboard up, covering {height} px" : "on-screen keyboard down");
			_imeVisible = visible;
			_imeHeight = height;
			_app?.SetInputPaneHeight(height);
		}

		// The surface runs under the display cutout, so the cutout's safe insets are what the app keeps its content clear
		// of; they move to another edge as the screen rotates. The system bars are hidden, and only swiped in over the
		// frame for a moment, so they are not part of it.
		var cutout = insets.DisplayCutout;
		var safeArea = cutout is null
			? ScreenInsets.None
			: new ScreenInsets(cutout.SafeInsetLeft, cutout.SafeInsetTop, cutout.SafeInsetRight, cutout.SafeInsetBottom);
		if (safeArea != _safeArea)
			Log.Info(LogTag, $"safe area: {safeArea.Left}, {safeArea.Top}, {safeArea.Right}, {safeArea.Bottom} px in from left, top, right, bottom");
		_safeArea = safeArea;
		_app?.SetSafeArea(safeArea);
		return view.OnApplyWindowInsets(insets) ?? insets;
	}

	void ISurfaceHolderCallback.SurfaceCreated(ISurfaceHolder holder)
	{
		// The size arrives with SurfaceChanged, which Android always calls next; the engine comes up there.
	}

	void ISurfaceHolderCallback.SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height)
	{
		if (_destroyed || _failed || width <= 0 || height <= 0)
			return;
		if (_engine is null)
		{
			BringUpEngine(holder, width, height);
			return;
		}
		try
		{
			if (_engine is IReattachableSurface { HasSurface: false } reattachable)
			{
				// Back on screen with a new surface: the engine, the app and everything they loaded carried on without one.
				_window = NativeWindow.FromSurface(holder.Surface!);
				reattachable.AttachSurface(new AndroidVkSurface(_window), width, height);
			}
			else
			{
				_engine.Resize(width, height);
			}
			_app!.Resize(width, height);
			StartFrames();
		}
		catch (Exception ex)
		{
			Fail("giving the engine its surface", ex);
		}
	}

	void ISurfaceHolderCallback.SurfaceDestroyed(ISurfaceHolder holder)
	{
		// The surface goes once this returns, so the engine must be done with it first. After OnDestroy the engine is
		// already gone, and this activity must not finish: on a relaunch, that would finish the activity replacing it.
		if (_destroyed)
			return;
		StopFrames();
		_input?.ReleaseContacts();
		if (_engine is IReattachableSurface reattachable)
		{
			// Keep the engine and the app: the next surface (the app coming back on screen) is attached to them.
			try
			{
				reattachable.DetachSurface();
			}
			catch (Exception ex)
			{
				Log.Error(LogTag, $"Detaching the engine from its surface failed: {ex}");
			}
			NativeWindow.Release(_window);
			_window = 0;
			return;
		}

		// An engine that cannot move to a new surface ends with this one, and the app with it. With no engine (it never
		// came up, and the failure is on screen) there is nothing to end: leaving the screen just leaves it.
		if (_engine is null)
			return;
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
		UpdateTextInput();
		_gamepads?.DrainRumble(_app.Rumble);
		_choreographer!.PostFrameCallback(this);
	}

	// Back: the app sees Escape pressed and released, and decides what it means.
	private void OnBack() => _input?.PressKey(Key.Escape, SystemClock.UptimeMillis());

	// Open the on-screen keyboard when the app starts wanting text, close it when it stops, and bring it back on a tap
	// when the user put it away while the app still wanted text.
	private void UpdateTextInput()
	{
		var wants = _app!.WantsTextInput;
		var reshow = _imeReshow;
		_imeReshow = false;
		if (wants && (!_imeRequested || (reshow && !_imeVisible)))
		{
			_imeRequested = true;
			var imm = (InputMethodManager?)GetSystemService(InputMethodService);
			_view!.RequestFocus();
			imm?.RestartInput(_view); // a fresh connection: nothing composed carries over from the last field
			imm?.ShowSoftInput(_view, ShowFlags.Implicit);
		}
		else if (!wants && _imeRequested)
		{
			_imeRequested = false;
			var imm = (InputMethodManager?)GetSystemService(InputMethodService);
			imm?.HideSoftInputFromWindow(_view!.WindowToken, HideSoftInputFlags.None);
			_view?.Connection?.Reset();
		}
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

			app.Load(_engine, new AndroidHostControl(this));
			// The host reports the initial size before the first frame, exactly like every later resize.
			app.Resize(width, height);
			// Controllers already connected are announced now the app is loaded, and later ones as they come and go.
			_gamepads?.Start(new Handler(Looper.MainLooper!));
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
	}

	private void StartFrames()
	{
		if (_framesRunning || !_resumed || _engine is null || _failed)
			return;
		if (_engine is IReattachableSurface { HasSurface: false })
			return; // off screen: frames start again when the next surface is attached
		_framesRunning = true;
		_input?.Accepting = true;
		_lastFrameNanos = 0; // the time paused is not a frame's delta
		_choreographer!.PostFrameCallback(this);
	}

	private void StopFrames()
	{
		_input?.Accepting = false; // nothing drains the app's input queue until frames run again
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

	// Draw under the system bars and the display cutout, hide the bars until swiped in, and keep the screen on. The
	// on-screen keyboard neither resizes nor pans the window: it covers the surface, and the app lays out clear of it.
	private void EnterFullscreen()
	{
		var window = Window!;
		window.AddFlags(WindowManagerFlags.KeepScreenOn);
		window.SetSoftInputMode(SoftInput.AdjustNothing | SoftInput.StateHidden);
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

	// The system's Back, from Android 13 on: Back no longer arrives as a key there.
	private sealed class BackCallback(LoomActivity activity) : Java.Lang.Object, IOnBackInvokedCallback
	{
		public void OnBackInvoked() => activity.OnBack();
	}
}
