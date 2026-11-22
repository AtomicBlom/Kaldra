using Loom.Core.Diagnostics;
using Loom.Input;

namespace Loom.Hosting.Desktop.Sdl;

/// <summary>
/// Reads gamepads through SDL 3 into Loom's neutral <see cref="InputEvent"/> stream, beside <see cref="DesktopInputPump"/>,
/// which keeps the keyboard and mouse (and stops reading GLFW's pads while this runs). SDL reads each pad with its own
/// driver where it has one, over HID: a DualSense or DualShock with its rumble, a Switch pad, a Steam Controller (grips
/// and all) without Steam's help, and XInput and Windows.Gaming.Input pads as they are.
/// </summary>
/// <remarks>
/// <para>
/// Only SDL's gamepad subsystem is started, with no window of its own; it is pumped on the host's thread once a frame
/// (<see cref="Update"/>), before the app reads its input. SDL is told to deliver pad events whatever has focus, and the
/// host's focus decides instead (<see cref="SetFocus"/>): unfocused, held buttons are released and the pads' events are
/// dropped; focused again, each pad's state is read afresh, so a stick held across an alt-tab reads where it is.
/// </para>
/// <para>
/// Pads take the lowest free of the snapshot's four slots as they connect and keep it until they go, so unplugging one
/// pad doesn't renumber the others. Single-threaded by design, and allocation-free per frame.
/// </para>
/// </remarks>
public sealed class SdlGamepadPump : IDisposable
{
	private const int Slots = 4;

	private readonly InputSink _sink;
	private readonly RumbleQueue? _rumble;
	private readonly Slot[] _slots = new Slot[Slots];
	private bool _focused = true;
	private bool _disposed;

	private struct Slot
	{
		public nint Pad;          // SDL_Gamepad*, 0 when free
		public uint Id;           // SDL's instance id
		public uint Buttons;      // held, by GamepadButton bit
		public Axes Levels;       // as last sent
	}

	[System.Runtime.CompilerServices.InlineArray(Sdl3.AxisCount)]
	private struct Axes
	{
		private float _first;
	}

	private SdlGamepadPump(InputSink sink, RumbleQueue? rumble)
	{
		_sink = sink;
		_rumble = rumble;
	}

	/// <summary>The SDL version in use, as <c>major.minor.micro</c>.</summary>
	public string Version { get; private set; } = "";

	/// <summary>
	/// Starts SDL's gamepad subsystem and returns a pump over it, or null (with the reason) where SDL3.dll is missing or
	/// won't start, in which case the host reads pads through GLFW as before.
	/// </summary>
	public static SdlGamepadPump? TryCreate(InputSink sink, RumbleQueue? rumble, out string reason)
	{
		ArgumentNullException.ThrowIfNull(sink);
		if (!Sdl3.TryLoad(out reason))
			return null;

		try
		{
			// Read before SDL_Init. Steam Controllers by SDL's own driver, rather than only through Steam; events whatever
			// has focus, since SDL has no window of its own to judge by (the host's focus gates them); SDL's device
			// notifications on its own thread, so they don't depend on GLFW's message pump; and no Ctrl+C handler of
			// SDL's, which would turn the console's break into an SDL quit event nobody reads
			Sdl3.Hint("SDL_JOYSTICK_HIDAPI_STEAM", "1");
			Sdl3.Hint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
			Sdl3.Hint("SDL_JOYSTICK_THREAD", "1");
			Sdl3.Hint("SDL_NO_SIGNAL_HANDLERS", "1");
			unsafe
			{
				if (Sdl3.SDL_Init(Sdl3.InitGamepad) == 0)
				{
					reason = $"SDL_Init failed: {Sdl3.Text(Sdl3.SDL_GetError())}";
					return null;
				}
			}
			int v = Sdl3.SDL_GetVersion();
			reason = "";
			return new SdlGamepadPump(sink, rumble) { Version = $"{v / 1000000}.{v / 1000 % 1000}.{v % 1000}" };
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
		{
			reason = $"SDL3.dll would not load: {ex}";
			return null;
		}
	}

	/// <summary>
	/// Per-frame: sends queued rumble to the pads, then drains SDL's events into the sink. Call once at the top of each
	/// frame, before the app resolves its input.
	/// </summary>
	public void Update(double deltaSeconds)
	{
		if (_disposed)
			return;

		if (_rumble is not null)
			while (_rumble.TryDequeue(out var command))
				Rumble(command);

		unsafe
		{
			SdlEvent e;
			while (Sdl3.SDL_PollEvent(&e) != 0)
				Handle(in e);
		}
	}

	/// <summary>
	/// The host window's focus. Losing it releases every held button and centres every stick and trigger; regaining it
	/// reads each pad as it is now.
	/// </summary>
	public void SetFocus(bool focused)
	{
		if (_disposed || focused == _focused)
			return;
		_focused = focused;
		for (int s = 0; s < Slots; s++)
		{
			if (_slots[s].Pad == 0)
				continue;
			if (focused)
				ReadAfresh(s);
			else
				Release(s);
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		for (int s = 0; s < Slots; s++)
			if (_slots[s].Pad != 0)
			{
				Sdl3.SDL_CloseGamepad(_slots[s].Pad);
				_slots[s] = default;
			}
		Sdl3.QuitSubSystem(Sdl3.InitGamepad); // only the pads: an audio library in the process may still be playing through SDL
	}

	private void Handle(in SdlEvent e)
	{
		switch (e.Type)
		{
			case Sdl3.EventGamepadAdded:
				Add(e.Which);
				break;
			case Sdl3.EventGamepadRemoved:
				Remove(e.Which);
				break;
			case Sdl3.EventGamepadButtonDown or Sdl3.EventGamepadButtonUp:
				if (_focused && SlotOf(e.Which) is var s and >= 0 && SdlGamepadMap.Button(e.Button) is { } button)
					SetButton(s, button, e.Type == Sdl3.EventGamepadButtonDown);
				break;
			case Sdl3.EventGamepadAxisMotion:
				if (_focused && SlotOf(e.Which) is var a and >= 0 && SdlGamepadMap.Axis(e.Axis) is { } axis)
					SetAxis(a, axis, SdlGamepadMap.Level(e.Axis, e.Value));
				break;
		}
	}

	private unsafe void Add(uint id)
	{
		if (SlotOf(id) >= 0)
			return;
		int s = Array.FindIndex(_slots, slot => slot.Pad == 0);
		var pad = Sdl3.SDL_OpenGamepad(id);
		if (pad == 0)
		{
			LoomLog.Warning($"SDL: a gamepad connected but would not open: {Sdl3.Text(Sdl3.SDL_GetError())}");
			return;
		}
		var name = Sdl3.Text(Sdl3.SDL_GetGamepadName(pad));
		if (s < 0)
		{
			LoomLog.Warning($"SDL: '{name}' connected, but all {Slots} gamepad slots are taken; it is ignored");
			Sdl3.SDL_CloseGamepad(pad);
			return;
		}
		_slots[s] = new Slot { Pad = pad, Id = id };
		// The real type: behind Steam's virtual Xbox pad, the controller the player holds
		ushort vendor = Sdl3.SDL_GetGamepadVendor(pad), product = Sdl3.SDL_GetGamepadProduct(pad);
		var type = SdlGamepadMap.Type(Sdl3.SDL_GetRealGamepadType(pad));
		if (type == GamepadType.Unknown)
			type = GamepadIdentity.FromIds(vendor, product);
		if (type == GamepadType.Unknown)
			type = GamepadIdentity.FromName(name);
		LoomLog.Info($"gamepad {s}: '{name}' ({type}, {vendor:x4}:{product:x4}, " +
			$"{SdlGamepadMap.ConnectionName(Sdl3.SDL_GetGamepadConnectionState(pad))}) through SDL {Version}");
		_sink(InputEvent.GamepadConnected(s, type, vendor, product));
		if (_focused)
			ReadAfresh(s);
	}

	private void Remove(uint id)
	{
		int s = SlotOf(id);
		if (s < 0)
			return;
		Sdl3.SDL_CloseGamepad(_slots[s].Pad);
		_slots[s] = default;
		LoomLog.Info($"gamepad {s} disconnected");
		_sink(InputEvent.GamepadDisconnected(s));   // the snapshot clears the slot's buttons and axes
	}

	private int SlotOf(uint id)
	{
		for (int s = 0; s < Slots; s++)
			if (_slots[s].Pad != 0 && _slots[s].Id == id)
				return s;
		return -1;
	}

	private void SetButton(int s, GamepadButton button, bool down)
	{
		uint bit = 1u << (int)button;
		if (((_slots[s].Buttons & bit) != 0) == down)
			return;
		_slots[s].Buttons ^= bit;
		_sink(down ? InputEvent.GamepadButtonDown(s, button) : InputEvent.GamepadButtonUp(s, button));
	}

	private void SetAxis(int s, GamepadAxis axis, float level)
	{
		if (_slots[s].Levels[(int)axis] == level)
			return;
		_slots[s].Levels[(int)axis] = level;
		_sink(InputEvent.GamepadAxisMoved(s, axis, level));
	}

	private void Release(int s)
	{
		for (int b = 0; b < 32; b++)
			if ((_slots[s].Buttons & (1u << b)) != 0)
				SetButton(s, (GamepadButton)b, false);
		for (int a = 0; a < Sdl3.AxisCount; a++)
			SetAxis(s, (GamepadAxis)a, 0f);
	}

	// Each button and axis as SDL has it now, sent where it differs from what was last sent
	private void ReadAfresh(int s)
	{
		var pad = _slots[s].Pad;
		for (int b = 0; b < Sdl3.ButtonCount; b++)
			if (SdlGamepadMap.Button(b) is { } button)
				SetButton(s, button, Sdl3.SDL_GetGamepadButton(pad, b) != 0);
		for (int a = 0; a < Sdl3.AxisCount; a++)
			if (SdlGamepadMap.Axis(a) is { } axis)
				SetAxis(s, axis, SdlGamepadMap.Level(a, Sdl3.SDL_GetGamepadAxis(pad, a)));
	}

	// SDL times the effect itself; a non-positive duration stops it
	private void Rumble(in RumbleCommand command)
	{
		if ((uint)command.PadIndex >= Slots || _slots[command.PadIndex].Pad == 0)
			return;
		var pad = _slots[command.PadIndex].Pad;
		if (command.Seconds <= 0f)
		{
			Sdl3.SDL_RumbleGamepad(pad, 0, 0, 0);
			return;
		}
		Sdl3.SDL_RumbleGamepad(pad, SdlGamepadMap.Motor(command.LowFrequency), SdlGamepadMap.Motor(command.HighFrequency),
			(uint)MathF.Ceiling(command.Seconds * 1000f));
	}
}

/// <summary>SDL's gamepad buttons, axes and levels as Loom's: pure, so the translation is tested without SDL.</summary>
internal static class SdlGamepadMap
{
	/// <summary>Loom's button for SDL's <c>SDL_GamepadButton</c>, or null for the ones Loom has no name for.</summary>
	public static GamepadButton? Button(int sdl) => sdl switch
	{
		0 => GamepadButton.South,
		1 => GamepadButton.East,
		2 => GamepadButton.West,
		3 => GamepadButton.North,
		4 => GamepadButton.Back,
		5 => GamepadButton.Guide,
		6 => GamepadButton.Start,
		7 => GamepadButton.LeftStick,
		8 => GamepadButton.RightStick,
		9 => GamepadButton.LeftBumper,
		10 => GamepadButton.RightBumper,
		11 => GamepadButton.DPadUp,
		12 => GamepadButton.DPadDown,
		13 => GamepadButton.DPadLeft,
		14 => GamepadButton.DPadRight,
		15 => GamepadButton.Misc1,
		16 => GamepadButton.RightPaddle1,
		17 => GamepadButton.LeftPaddle1,
		18 => GamepadButton.RightPaddle2,
		19 => GamepadButton.LeftPaddle2,
		20 => GamepadButton.Touchpad,
		_ => null,
	};

	/// <summary>Loom's axis for SDL's <c>SDL_GamepadAxis</c>: the same order.</summary>
	public static GamepadAxis? Axis(int sdl) => sdl is >= 0 and < Sdl3.AxisCount ? (GamepadAxis)sdl : null;

	/// <summary>
	/// A level as Loom has it: sticks from SDL's −32768..32767 to −1..1, up negative as SDL and GLFW both have it;
	/// triggers from 0..32767 to 0..1.
	/// </summary>
	public static float Level(int sdlAxis, short value) => sdlAxis >= 4
		? Math.Clamp(value / 32767f, 0f, 1f)
		: Math.Clamp(value / 32767f, -1f, 1f);

	/// <summary>A motor's strength, 0..1, as SDL's 0..65535.</summary>
	public static ushort Motor(float strength) => (ushort)MathF.Round(Math.Clamp(strength, 0f, 1f) * 65535f);

	/// <summary>Loom's type for SDL's <c>SDL_GamepadType</c> (its "standard" is a pad SDL doesn't know: Unknown).</summary>
	public static GamepadType Type(int sdl) => sdl switch
	{
		2 => GamepadType.Xbox360,
		3 => GamepadType.XboxOne,
		4 => GamepadType.PS3,
		5 => GamepadType.PS4,
		6 => GamepadType.PS5,
		7 => GamepadType.SwitchPro,
		8 => GamepadType.JoyConLeft,
		9 => GamepadType.JoyConRight,
		10 => GamepadType.JoyConPair,
		11 => GamepadType.GameCube,
		12 => GamepadType.Steam,
		_ => GamepadType.Unknown,
	};

	public static string ConnectionName(int state) => state switch
	{
		1 => "wired",
		2 => "wireless",
		_ => "connection unknown",
	};
}
