using Android.Hardware.Input;
using Android.OS;
using Android.Views;

using Loom.Core.Hosting;
using Loom.Input;

using InputDevice = Android.Views.InputDevice;
using InputEvent = Loom.Input.InputEvent;

namespace Loom.Hosting.Android;

/// <summary>
/// Game controllers on Android: Bluetooth and USB gamepads, each given one of <see cref="SlotCount"/> pad indices while
/// it is connected, their buttons (key events) and sticks, triggers and hat (joystick motion) turned into the neutral
/// gamepad events, and the app's rumble played on their motors.
/// </summary>
/// <remarks>
/// Android has no gamepad API of its own: a controller is an <see cref="InputDevice"/> whose sources include gamepad or
/// joystick, its buttons arrive as <see cref="KeyEvent"/>s and its axes as joystick <see cref="MotionEvent"/>s. The
/// sticks are reported as they are (y down, as on the desktop), snapped to zero inside the controller's own flat region
/// so a resting stick is still; the hat that many controllers (a PlayStation one among them) report their D-pad on
/// becomes the D-pad buttons. Runs on the UI thread, which the activity's frames also run on.
/// </remarks>
internal sealed class AndroidGamepads : Java.Lang.Object, InputManager.IInputDeviceListener
{
	/// <summary>How many controllers can be connected at once; a further one is ignored until a slot frees.</summary>
	public const int SlotCount = 4;

	private const int AxisCount = 6;
	private static readonly Axis[] Axes = [Axis.X, Axis.Y, Axis.Z, Axis.Rz, Axis.Ltrigger, Axis.Rtrigger];
	private static readonly GamepadAxis[] NeutralAxes =
		[GamepadAxis.LeftX, GamepadAxis.LeftY, GamepadAxis.RightX, GamepadAxis.RightY, GamepadAxis.LeftTrigger, GamepadAxis.RightTrigger];

	private readonly IHostedApplication _app;
	private readonly InputManager? _inputManager;

	// Per slot: the Android device id in it (-1 when free), the buttons held, the last value sent per axis, each axis's
	// flat region, and the hat's last direction.
	private readonly int[] _deviceIds = [-1, -1, -1, -1];
	private readonly uint[] _held = new uint[SlotCount];
	private readonly float[,] _axisValues = new float[SlotCount, AxisCount];
	private readonly float[,] _axisFlat = new float[SlotCount, AxisCount];
	private readonly bool[,] _triggerFromPedals = new bool[SlotCount, 2];
	private readonly int[] _hatX = new int[SlotCount];
	private readonly int[] _hatY = new int[SlotCount];

	public AndroidGamepads(IHostedApplication app, InputManager? inputManager)
	{
		_app = app;
		_inputManager = inputManager;
	}

	/// <summary>
	/// Start following controllers: every one already connected is announced, and later ones as they come and go.
	/// </summary>
	public void Start(Handler handler)
	{
		_inputManager?.RegisterInputDeviceListener(this, handler);
		foreach (var id in InputDevice.GetDeviceIds() ?? [])
			Connect(id);
	}

	/// <summary>Stop following controllers. Their pads stay as they are; the app is going away.</summary>
	public void Stop() => _inputManager?.UnregisterInputDeviceListener(this);

	/// <summary>Queue a controller button's press or release. Returns whether the event was a controller's.</summary>
	public bool OnKey(KeyEvent e)
	{
		var slot = SlotOf(e.DeviceId);
		if (slot < 0 || AndroidKeyMap.ToGamepadButton(e.KeyCode) is not { } button)
			return false;
		if (e.RepeatCount == 0 && e.Action is KeyEventActions.Down or KeyEventActions.Up)
			SetButton(slot, button, e.Action == KeyEventActions.Down, e.EventTime);
		return true; // a repeat is still the controller's, and taken
	}

	/// <summary>Queue a controller's stick, trigger and hat movement. Returns whether the event was a controller's.</summary>
	public bool OnMotion(MotionEvent e)
	{
		if (!e.IsFromSource(InputSourceType.Joystick) || e.ActionMasked != MotionEventActions.Move)
			return false;
		var slot = SlotOf(e.DeviceId);
		if (slot < 0)
			return false;

		var timestamp = e.EventTime;
		for (var a = 0; a < AxisCount; a++)
		{
			var value = ReadAxis(e, slot, a);
			if (value == _axisValues[slot, a])
				continue;
			_axisValues[slot, a] = value;
			_app.QueueInput(InputEvent.GamepadAxisMoved(slot, NeutralAxes[a], value, timestamp));
		}

		var hatX = Math.Sign(MathF.Round(e.GetAxisValue(Axis.HatX)));
		var hatY = Math.Sign(MathF.Round(e.GetAxisValue(Axis.HatY)));
		if (hatX != _hatX[slot])
		{
			SetHat(slot, _hatX[slot], hatX, GamepadButton.DPadLeft, GamepadButton.DPadRight, timestamp);
			_hatX[slot] = hatX;
		}
		if (hatY != _hatY[slot])
		{
			SetHat(slot, _hatY[slot], hatY, GamepadButton.DPadUp, GamepadButton.DPadDown, timestamp);
			_hatY[slot] = hatY;
		}
		return true;
	}

	/// <summary>
	/// Release every controller button held, for the window losing focus: the releases of buttons held across it never
	/// come. The axes are left; their next movement reports them.
	/// </summary>
	public void ReleaseAll(long timestamp)
	{
		for (var slot = 0; slot < SlotCount; slot++)
			ReleaseSlot(slot, timestamp);
	}

	/// <summary>Play the app's queued rumble on the controllers' motors.</summary>
	public void DrainRumble(RumbleQueue rumble)
	{
		while (rumble.TryDequeue(out var command))
		{
			if ((uint)command.PadIndex >= SlotCount || _deviceIds[command.PadIndex] < 0)
				continue;
			try
			{
				Rumble(_deviceIds[command.PadIndex], command);
			}
			catch (Java.Lang.Exception)
			{
				// A controller that refuses a vibration (one with no motors Android can drive) just does not rumble.
			}
		}
	}

	void InputManager.IInputDeviceListener.OnInputDeviceAdded(int deviceId) => Connect(deviceId);

	void InputManager.IInputDeviceListener.OnInputDeviceRemoved(int deviceId) => Disconnect(deviceId);

	void InputManager.IInputDeviceListener.OnInputDeviceChanged(int deviceId)
	{
		// A device that stops being a controller leaves its pad; one that becomes one takes a pad.
		if (IsController(InputDevice.GetDevice(deviceId)))
			Connect(deviceId);
		else
			Disconnect(deviceId);
	}

	private void Connect(int deviceId)
	{
		if (SlotOf(deviceId) >= 0)
			return;
		var device = InputDevice.GetDevice(deviceId);
		if (!IsController(device))
			return;
		var slot = Array.IndexOf(_deviceIds, -1);
		if (slot < 0)
			return; // every pad is taken

		_deviceIds[slot] = deviceId;
		_held[slot] = 0;
		_hatX[slot] = _hatY[slot] = 0;
		for (var a = 0; a < AxisCount; a++)
		{
			_axisValues[slot, a] = 0f;
			_axisFlat[slot, a] = device!.GetMotionRange(Axes[a], InputSourceType.Joystick)?.Flat ?? 0f;
		}
		// A controller without the trigger axes reports its triggers as the brake and gas pedals.
		_triggerFromPedals[slot, 0] = device!.GetMotionRange(Axis.Ltrigger, InputSourceType.Joystick) is null
			&& device.GetMotionRange(Axis.Brake, InputSourceType.Joystick) is not null;
		_triggerFromPedals[slot, 1] = device.GetMotionRange(Axis.Rtrigger, InputSourceType.Joystick) is null
			&& device.GetMotionRange(Axis.Gas, InputSourceType.Joystick) is not null;
		_app.QueueInput(InputEvent.GamepadConnected(slot, SystemClock.UptimeMillis()));
	}

	private void Disconnect(int deviceId)
	{
		var slot = SlotOf(deviceId);
		if (slot < 0)
			return;
		var timestamp = SystemClock.UptimeMillis();
		ReleaseSlot(slot, timestamp);
		_deviceIds[slot] = -1;
		_app.QueueInput(InputEvent.GamepadDisconnected(slot, timestamp));
	}

	private void ReleaseSlot(int slot, long timestamp)
	{
		var held = _held[slot];
		_held[slot] = 0;
		while (held != 0)
		{
			var bit = System.Numerics.BitOperations.TrailingZeroCount(held);
			held &= held - 1;
			_app.QueueInput(InputEvent.GamepadButtonUp(slot, (GamepadButton)bit, timestamp));
		}
		_hatX[slot] = _hatY[slot] = 0;
	}

	private void SetButton(int slot, GamepadButton button, bool down, long timestamp)
	{
		var bit = 1u << (int)button;
		if (down == ((_held[slot] & bit) != 0))
			return; // already in that state: the D-pad can arrive both as keys and as a hat
		_held[slot] = down ? _held[slot] | bit : _held[slot] & ~bit;
		_app.QueueInput(down ? InputEvent.GamepadButtonDown(slot, button, timestamp) : InputEvent.GamepadButtonUp(slot, button, timestamp));
	}

	// One hat axis moved from `from` to `to` (each -1, 0 or 1): release the direction it left, press the one it reached.
	private void SetHat(int slot, int from, int to, GamepadButton negative, GamepadButton positive, long timestamp)
	{
		if (from != 0)
			SetButton(slot, from < 0 ? negative : positive, down: false, timestamp);
		if (to != 0)
			SetButton(slot, to < 0 ? negative : positive, down: true, timestamp);
	}

	private float ReadAxis(MotionEvent e, int slot, int a)
	{
		var axis = Axes[a];
		if (a >= 4 && _triggerFromPedals[slot, a - 4])
			axis = a == 4 ? Axis.Brake : Axis.Gas;
		var value = e.GetAxisValue(axis);
		return MathF.Abs(value) <= _axisFlat[slot, a] ? 0f : value;
	}

	private int SlotOf(int deviceId) => deviceId < 0 ? -1 : Array.IndexOf(_deviceIds, deviceId);

	private static bool IsController(InputDevice? device)
	{
		if (device is null || device.IsVirtual)
			return false;
		var sources = device.Sources;
		return (sources & InputSourceType.Gamepad) == InputSourceType.Gamepad
			|| (sources & InputSourceType.Joystick) == InputSourceType.Joystick;
	}

	// Drive the controller's motors for the command: the first with the low-frequency strength, the second with the
	// high, for the command's duration; a stop (or nothing to play) cancels whatever is playing.
	private static void Rumble(int deviceId, in RumbleCommand command)
	{
		if (!OperatingSystem.IsAndroidVersionAtLeast(31))
			return; // a controller's own motors are reachable from Android 12
		var manager = InputDevice.GetDevice(deviceId)?.VibratorManager;
		var ids = manager?.GetVibratorIds();
		if (manager is null || ids is null || ids.Length == 0)
			return;

		var milliseconds = (long)(command.Seconds * 1000f);
		var low = Amplitude(command.LowFrequency);
		var high = Amplitude(command.HighFrequency);
		if (milliseconds <= 0 || (low == 0 && high == 0))
		{
			manager.Cancel();
			return;
		}

		var combined = CombinedVibration.StartParallel()!;
		for (var i = 0; i < ids.Length; i++)
		{
			var amplitude = i == 0 ? low : high;
			if (ids.Length == 1)
				amplitude = Math.Max(low, high);
			if (amplitude > 0)
				combined.AddVibrator(ids[i], VibrationEffect.CreateOneShot(milliseconds, amplitude)!);
		}
		manager.Vibrate(combined.Combine()!);
	}

	// A neutral motor strength (0 to 1) as an Android vibration amplitude (1 to 255; 0 is off).
	private static int Amplitude(float strength) => strength <= 0f ? 0 : Math.Clamp((int)MathF.Round(strength * 255f), 1, 255);
}
