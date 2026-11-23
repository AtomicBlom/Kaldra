namespace BinaryVibrance.Kaldra.Input;

/// <summary>Where a <see cref="BindingCapture"/> has got.</summary>
public enum CaptureState
{
	/// <summary>Nothing new pressed yet.</summary>
	Waiting,

	/// <summary>A control was pressed: <see cref="BindingCapture.Captured"/> says which.</summary>
	Captured,

	/// <summary>The cancel key (or button) was pressed.</summary>
	Cancelled,
}

/// <summary>
/// Listens for the next control the player presses, for rebinding an action: "press a key", "press a button". Started
/// from the snapshot as it is, it ignores everything already held (the key or button that chose the action to rebind)
/// until it is let go, so only a fresh press counts. Poll once a frame with the frame's snapshot.
/// </summary>
/// <remarks>
/// A key or mouse button, a pad button, a trigger squeezed past half way (its axis), or a stick pushed past
/// <see cref="StickThreshold"/> (that direction of its axis) each count, from the families <see cref="Families"/> takes.
/// Which pad is noted (<see cref="Pad"/>), though a binding made from it usually reads <see cref="Input.Pad.Last"/>.
/// </remarks>
public sealed class BindingCapture
{
	private const float Release = 0.25f;   // an axis held at the start counts again once back under this

	private readonly ulong[] _heldKeys = new ulong[InputSnapshot.KeyWordCount];
	private uint _heldMouse;
	private readonly uint[] _heldButtons = new uint[InputSnapshot.PadSlots];
	private readonly bool[] _heldAxes = new bool[InputSnapshot.PadSlots * 6];

	/// <summary>Starts listening, ignoring whatever <paramref name="snapshot"/> has held now.</summary>
	/// <param name="families">Which devices may answer: <see cref="InputDevice.Keyboard"/>, <see cref="InputDevice.Mouse"/>,
	/// <see cref="InputDevice.Gamepad"/>, or several.</param>
	/// <param name="snapshot">The input as it is now.</param>
	public BindingCapture(InputDevice families, InputSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		Families = families;
		for (int w = 0; w < _heldKeys.Length; w++)
			_heldKeys[w] = snapshot.KeyWordAt(w);
		_heldMouse = snapshot.MouseButtonBits;
		for (int pad = 0; pad < InputSnapshot.PadSlots; pad++)
		{
			_heldButtons[pad] = snapshot.PadButtonBits(pad);
			var state = snapshot.Gamepad(pad);
			for (int a = 0; a < 6; a++)
				_heldAxes[pad * 6 + a] = MathF.Abs(state.Axis((GamepadAxis)a)) > Release;
		}
	}

	/// <summary>The devices that may answer.</summary>
	public InputDevice Families { get; }

	/// <summary>The key that cancels (default Escape; null for none). It is never captured.</summary>
	public Key? CancelKey { get; init; } = Key.Escape;

	/// <summary>The pad button that cancels (default none). It is never captured.</summary>
	public GamepadButton? CancelButton { get; init; }

	/// <summary>How far a stick must go to count (default 0.6).</summary>
	public float StickThreshold { get; init; } = 0.6f;

	/// <summary>How far a trigger must go to count (default 0.5).</summary>
	public float TriggerThreshold { get; init; } = 0.5f;

	/// <summary>The control pressed, once <see cref="Poll"/> has said <see cref="CaptureState.Captured"/>.</summary>
	public InputControl Captured { get; private set; }

	/// <summary>The pad it was pressed on, for a pad's control; -1 otherwise.</summary>
	public int Pad { get; private set; } = -1;

	/// <summary>Looks at this frame's input: a fresh press of the cancel key or button, or of any control the families
	/// allow, ends it.</summary>
	public CaptureState Poll(InputSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		// Let go of what was held at the start, so pressing it again counts
		for (int w = 0; w < _heldKeys.Length; w++)
			_heldKeys[w] &= snapshot.KeyWordAt(w);
		_heldMouse &= snapshot.MouseButtonBits;
		for (int pad = 0; pad < InputSnapshot.PadSlots; pad++)
		{
			_heldButtons[pad] &= snapshot.PadButtonBits(pad);
			var state = snapshot.Gamepad(pad);
			for (int a = 0; a < 6; a++)
				if (MathF.Abs(state.Axis((GamepadAxis)a)) < Release)
					_heldAxes[pad * 6 + a] = false;
		}

		if (CancelKey is { } cancel && Fresh(snapshot, cancel))
			return CaptureState.Cancelled;
		for (int pad = 0; pad < InputSnapshot.PadSlots; pad++)
			if (CancelButton is { } button && FreshButton(snapshot, pad, button))
				return CaptureState.Cancelled;

		if (Families.HasFlag(InputDevice.Keyboard))
			for (int k = 0; k < InputSnapshot.KeyWordCount * 64; k++)
				if ((Key)k != CancelKey && (Key)k != Key.Unknown && Fresh(snapshot, (Key)k))
					return Took(InputControl.Of((Key)k), -1);

		if (Families.HasFlag(InputDevice.Mouse))
			for (int b = 0; b < 32; b++)
				if ((snapshot.MouseButtonBits & ~_heldMouse & (1u << b)) != 0)
					return Took(InputControl.Of((MouseButton)b), -1);

		if (Families.HasFlag(InputDevice.Gamepad))
			for (int pad = 0; pad < InputSnapshot.PadSlots; pad++)
			{
				var state = snapshot.Gamepad(pad);
				if (!state.Connected)
					continue;
				for (int b = 0; b < 32; b++)
					if (b != (int?)CancelButton && FreshButton(snapshot, pad, (GamepadButton)b))
						return Took(InputControl.Of((GamepadButton)b), pad);
				for (int a = 0; a < 6; a++)
				{
					if (_heldAxes[pad * 6 + a])
						continue;
					float v = state.Axis((GamepadAxis)a);
					bool trigger = a >= (int)GamepadAxis.LeftTrigger;
					if (MathF.Abs(v) >= (trigger ? TriggerThreshold : StickThreshold))
						return Took(InputControl.Of((GamepadAxis)a, trigger ? 0 : MathF.Sign(v)), pad);
				}
			}

		return CaptureState.Waiting;
	}

	private CaptureState Took(InputControl control, int pad)
	{
		Captured = control;
		Pad = pad;
		return CaptureState.Captured;
	}

	private bool Fresh(InputSnapshot snapshot, Key key)
	{
		int w = (int)key >> 6;
		ulong bit = 1UL << ((int)key & 63);
		return (snapshot.KeyWordAt(w) & ~_heldKeys[w] & bit) != 0;
	}

	private bool FreshButton(InputSnapshot snapshot, int pad, GamepadButton button)
		=> (snapshot.PadButtonBits(pad) & ~_heldButtons[pad] & (1u << (int)button)) != 0;
}
