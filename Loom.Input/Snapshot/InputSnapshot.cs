using System.Numerics;

namespace Loom.Input;

/// <summary>
/// The per-frame poll surface over preallocated buffers: which keys/buttons are held, where the pointer is, the
/// gamepads' state, and the active touches. A snapshot is <b>advanced</b> once per frame by an event stream
/// (<see cref="Advance"/>): it copies the current state to a previous frame, then folds the stream in, so edge
/// queries (<see cref="Pressed(Key)"/>/<see cref="Released(Key)"/>) compare this frame against the last. Every
/// input layer owns its own snapshot advanced by its own surviving event stream; the bottom-of-stack snapshot is
/// what the app polls.
/// </summary>
/// <remarks>
/// The layer stack resolves a frame's input once, so a snapshot is valid for that whole frame — including every
/// fixed-step update within it. A <see cref="Pressed(Key)"/> edge is therefore visible to <i>all</i> fixed steps
/// of a frame; guard once-per-press logic accordingly rather than assuming one fixed step per edge.
/// </remarks>
public sealed class InputSnapshot
{
	// 256 key slots (four 64-bit words) — comfortably covers the dense Key enum with headroom.
	internal const int KeyWordCount = 4;
	internal const int PadSlots = 4;
	private const int AxesPerPad = 6;

	private readonly ulong[] _keys = new ulong[KeyWordCount];
	private readonly ulong[] _keysPrev = new ulong[KeyWordCount];
	private uint _mouseButtons;
	private uint _mouseButtonsPrev;
	private Vector2 _pointer;
	private Vector2 _pointerPrev;
	private Vector2 _wheel;

	private readonly uint[] _padButtons = new uint[PadSlots];
	private readonly uint[] _padButtonsPrev = new uint[PadSlots];
	private readonly bool[] _padConnected = new bool[PadSlots];
	private readonly bool[] _padConnectedPrev = new bool[PadSlots];
	private readonly float[] _padAxes = new float[PadSlots * AxesPerPad];
	private readonly GamepadType[] _padType = new GamepadType[PadSlots];
	private readonly uint[] _padIds = new uint[PadSlots];   // vendor << 16 | product, as the connect event carried them

	private readonly TouchPoint[] _touches;
	private int _touchCount;

	private readonly RumbleQueue? _rumble;

	// How far a device must go to count as used (LastDevice): past a stick's drift, a resting finger on a trigger, a
	// nudged mouse
	private const float StickToUse = 0.4f, TriggerToUse = 0.3f, MouseMoveToUse = 8f;
	private Vector2? _moveAnchor;   // where the mouse lay when another device was last used

	/// <summary>Creates an empty snapshot. <paramref name="rumble"/> is the shared queue
	/// <see cref="Rumble"/> enqueues onto (null disables rumble output for this snapshot).</summary>
	public InputSnapshot(RumbleQueue? rumble = null, int maxTouches = 16)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(maxTouches, 1);
		_touches = new TouchPoint[maxTouches];
		_rumble = rumble;
	}

	/// <summary>The pointer position in surface pixels, as of this frame.</summary>
	public Vector2 PointerPosition => _pointer;

	/// <summary>How far the pointer moved since last frame.</summary>
	public Vector2 PointerDelta => _pointer - _pointerPrev;

	/// <summary>Accumulated scroll-wheel delta this frame (X horizontal, Y vertical).</summary>
	public Vector2 Wheel => _wheel;

	/// <summary>The active touch contacts this frame.</summary>
	public ReadOnlySpan<TouchPoint> Touches => _touches.AsSpan(0, _touchCount);

	/// <summary>Whether <paramref name="key"/> is currently held.</summary>
	public bool Down(Key key) => (_keys[Word(key)] & Bit(key)) != 0;

	/// <summary>Whether <paramref name="key"/> transitioned to held this frame.</summary>
	public bool Pressed(Key key) => (_keys[Word(key)] & Bit(key)) != 0 && (_keysPrev[Word(key)] & Bit(key)) == 0;

	/// <summary>Whether <paramref name="key"/> transitioned to released this frame.</summary>
	public bool Released(Key key) => (_keys[Word(key)] & Bit(key)) == 0 && (_keysPrev[Word(key)] & Bit(key)) != 0;

	/// <summary>Whether <paramref name="button"/> is currently held.</summary>
	public bool Down(MouseButton button) => (_mouseButtons & Bit(button)) != 0;

	/// <summary>Whether <paramref name="button"/> transitioned to held this frame.</summary>
	public bool Pressed(MouseButton button) => (_mouseButtons & Bit(button)) != 0 && (_mouseButtonsPrev & Bit(button)) == 0;

	/// <summary>Whether <paramref name="button"/> transitioned to released this frame.</summary>
	public bool Released(MouseButton button) => (_mouseButtons & Bit(button)) == 0 && (_mouseButtonsPrev & Bit(button)) != 0;

	/// <summary>Whether <paramref name="button"/> is held on the pad last used (<see cref="Pad.Last"/>).</summary>
	public bool Down(GamepadButton button) => Gamepad(Pad.Last).Down(button);

	/// <summary>Whether <paramref name="button"/> was pressed this frame on the pad last used, which pressing it makes it.</summary>
	public bool Pressed(GamepadButton button) => Gamepad(Pad.Last).Pressed(button);

	/// <summary>Whether <paramref name="button"/> was released this frame on the pad last used.</summary>
	public bool Released(GamepadButton button) => Gamepad(Pad.Last).Released(button);

	/// <summary>
	/// The device the player last used: <see cref="InputDevice.Keyboard"/>, <see cref="InputDevice.Mouse"/>,
	/// <see cref="InputDevice.Gamepad"/> (which one in <see cref="LastPad"/>) or <see cref="InputDevice.Touch"/>;
	/// <see cref="InputDevice.None"/> until something is. Moved by a key or button going down, the wheel, a mouse moved
	/// more than a few pixels, a touch, a stick pushed past 0.4 or a trigger past 0.3 — not by a stick's drift. For
	/// prompts that follow what the player has in hand.
	/// </summary>
	public InputDevice LastDevice { get; private set; }

	/// <summary>The gamepad last used (0..3), or -1 if none has been since it connected.</summary>
	public int LastPad { get; private set; } = -1;

	/// <summary>
	/// A by-value view of gamepad <paramref name="index"/> (0..3), or of the pad last used for <see cref="Pad.Last"/>
	/// (the first connected one before any is used); a disconnected default for out-of-range.
	/// </summary>
	public GamepadState Gamepad(int index)
	{
		if (index == Pad.Last)
			index = LastPad >= 0 ? LastPad : Math.Max(0, Array.IndexOf(_padConnected, true));
		if ((uint)index >= PadSlots)
			return default;

		var axisBase = index * AxesPerPad;
		return new GamepadState(
			_padConnected[index], _padConnectedPrev[index],
			_padButtons[index], _padButtonsPrev[index],
			_padAxes[axisBase + (int)GamepadAxis.LeftX],
			_padAxes[axisBase + (int)GamepadAxis.LeftY],
			_padAxes[axisBase + (int)GamepadAxis.RightX],
			_padAxes[axisBase + (int)GamepadAxis.RightY],
			_padAxes[axisBase + (int)GamepadAxis.LeftTrigger],
			_padAxes[axisBase + (int)GamepadAxis.RightTrigger],
			_padType[index], _padIds[index]);
	}

	/// <summary>Enqueues a rumble effect for a gamepad (a no-op if this snapshot has no rumble queue).</summary>
	public void Rumble(int padIndex, float lowFrequency, float highFrequency, float seconds)
		=> _rumble?.Enqueue(new RumbleCommand(padIndex, lowFrequency, highFrequency, seconds));

	/// <summary>
	/// Advances the snapshot one frame: rolls the current state into the previous frame (so edge queries work),
	/// resets per-frame accumulators (wheel, pointer delta baseline, hotplug/touch lifecycle), then folds
	/// <paramref name="events"/> into the current state.
	/// </summary>
	public void Advance(ReadOnlySpan<InputEvent> events)
	{
		BeginFrame();
		foreach (ref readonly var e in events)
			Apply(in e);
	}

	private void BeginFrame()
	{
		Array.Copy(_keys, _keysPrev, KeyWordCount);
		_mouseButtonsPrev = _mouseButtons;
		_pointerPrev = _pointer;
		_wheel = Vector2.Zero;
		Array.Copy(_padButtons, _padButtonsPrev, PadSlots);
		Array.Copy(_padConnected, _padConnectedPrev, PadSlots);

		// Drop contacts that ended or were cancelled last frame; keep live ones held at their last phase.
		var live = 0;
		for (var i = 0; i < _touchCount; i++)
		{
			var phase = _touches[i].Phase;
			if (phase is TouchPhase.Ended or TouchPhase.Canceled)
				continue;
			_touches[live++] = _touches[i];
		}

		_touchCount = live;
	}

	private void Apply(in InputEvent e)
	{
		switch (e.Kind)
		{
			case InputEventKind.KeyDown:
				_keys[Word(e.Key)] |= Bit(e.Key);
				Used(InputDevice.Keyboard);
				break;
			case InputEventKind.KeyUp:
				_keys[Word(e.Key)] &= ~Bit(e.Key);
				break;
			case InputEventKind.MouseButtonDown:
				_mouseButtons |= Bit(e.Button);
				Used(InputDevice.Mouse);
				break;
			case InputEventKind.MouseButtonUp:
				_mouseButtons &= ~Bit(e.Button);
				break;
			case InputEventKind.PointerMove:
				_pointer = new Vector2(e.X, e.Y);
				if (e.PointerType == PointerDeviceType.Mouse && LastDevice != InputDevice.Mouse)
				{
					// A mouse moved far enough from where it lay when something else was last used; not a nudged desk
					_moveAnchor ??= _pointer;
					if (Vector2.Distance(_pointer, _moveAnchor.Value) > MouseMoveToUse)
						Used(InputDevice.Mouse);
				}
				break;
			case InputEventKind.Wheel:
				_wheel += new Vector2(e.X, e.Y);
				Used(InputDevice.Mouse);
				break;
			case InputEventKind.GamepadButtonDown:
				if ((uint)e.PadIndex < PadSlots)
				{
					_padButtons[e.PadIndex] |= 1u << e.Code;
					Used(InputDevice.Gamepad, e.PadIndex);
				}
				break;
			case InputEventKind.GamepadButtonUp:
				if ((uint)e.PadIndex < PadSlots)
					_padButtons[e.PadIndex] &= ~(1u << e.Code);
				break;
			case InputEventKind.GamepadAxis:
				if ((uint)e.PadIndex < PadSlots && (uint)e.Code < AxesPerPad)
				{
					_padAxes[(e.PadIndex * AxesPerPad) + e.Code] = e.Value;
					// Past drift: a stick well off centre, a trigger squeezed
					bool trigger = e.Code >= (int)GamepadAxis.LeftTrigger;
					if (MathF.Abs(e.Value) > (trigger ? TriggerToUse : StickToUse))
						Used(InputDevice.Gamepad, e.PadIndex);
				}
				break;
			case InputEventKind.GamepadConnected:
				if ((uint)e.PadIndex < PadSlots)
				{
					_padConnected[e.PadIndex] = true;
					_padType[e.PadIndex] = (GamepadType)e.Code;
					_padIds[e.PadIndex] = e.CodePoint;
					ClearPad(e.PadIndex);
				}

				break;
			case InputEventKind.GamepadDisconnected:
				if ((uint)e.PadIndex < PadSlots)
				{
					_padConnected[e.PadIndex] = false;
					_padType[e.PadIndex] = GamepadType.Unknown;
					_padIds[e.PadIndex] = 0;
					ClearPad(e.PadIndex);
					if (LastPad == e.PadIndex)
						LastPad = -1;   // the next pad used takes over; until then Pad.Last reads the first connected
				}

				break;
			case InputEventKind.Touch:
				ApplyTouch(in e);
				Used(InputDevice.Touch);
				break;
			case InputEventKind.Text:
			case InputEventKind.None:
			default:
				// Text carries no polled level state; it is observed only as an event.
				break;
		}
	}

	private void ApplyTouch(in InputEvent e)
	{
		var point = new TouchPoint(e.PointerId, new Vector2(e.X, e.Y), e.Phase);
		for (var i = 0; i < _touchCount; i++)
		{
			if (_touches[i].Id == e.PointerId)
			{
				_touches[i] = point;
				return;
			}
		}

		if (_touchCount < _touches.Length)
			_touches[_touchCount++] = point;
	}

	// A (dis)connecting pad holds nothing, so clear its buttons and axes. On disconnect this turns a button held at
	// unplug into a clean release edge this frame (BeginFrame already snapshotted it as previously-down) rather than
	// stranding it: GamepadState.Down reports the mask without consulting Connected, so an uncleared button would
	// read held forever. On connect it discards any stale state left by a prior pad at the same index.
	private void ClearPad(int index)
	{
		_padButtons[index] = 0;
		var axisBase = index * AxesPerPad;
		for (var a = 0; a < AxesPerPad; a++)
			_padAxes[axisBase + a] = 0f;
	}

	private void Used(InputDevice device, int pad = -1)
	{
		if (device != InputDevice.Mouse)
			_moveAnchor = null;
		LastDevice = device;
		if (device == InputDevice.Gamepad)
			LastPad = pad;
	}

	internal ulong KeyWordAt(int word) => _keys[word];

	internal uint MouseButtonBits => _mouseButtons;

	internal uint PadButtonBits(int pad) => _padButtons[pad];

	private static int Word(Key key) => (int)key >> 6;

	private static ulong Bit(Key key) => 1UL << ((int)key & 63);

	private static uint Bit(MouseButton button) => 1u << (int)button;
}
