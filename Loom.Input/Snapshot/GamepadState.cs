namespace Loom.Input;

/// <summary>
/// A read-only view of one gamepad's state for a frame, snapshotted by value from an <see cref="InputSnapshot"/>
/// via <see cref="InputSnapshot.Gamepad"/>. Carries both this frame's and last frame's button masks so button
/// edges (<see cref="Pressed"/>/<see cref="Released"/>) and hotplug edges
/// (<see cref="JustConnected"/>/<see cref="JustDisconnected"/>) resolve without another lookup.
/// </summary>
public readonly struct GamepadState
{
	private readonly uint _buttons;
	private readonly uint _prevButtons;
	private readonly bool _prevConnected;
	private readonly float _leftX;
	private readonly float _leftY;
	private readonly float _rightX;
	private readonly float _rightY;
	private readonly float _leftTrigger;
	private readonly float _rightTrigger;
	private readonly uint _ids;

	internal GamepadState(bool connected, bool prevConnected, uint buttons, uint prevButtons,
		float leftX, float leftY, float rightX, float rightY, float leftTrigger, float rightTrigger,
		GamepadType type = GamepadType.Unknown, uint ids = 0)
	{
		Type = type;
		_ids = ids;
		Connected = connected;
		_prevConnected = prevConnected;
		_buttons = buttons;
		_prevButtons = prevButtons;
		_leftX = leftX;
		_leftY = leftY;
		_rightX = rightX;
		_rightY = rightY;
		_leftTrigger = leftTrigger;
		_rightTrigger = rightTrigger;
	}

	/// <summary>Whether a pad is connected at this index.</summary>
	public bool Connected { get; }

	/// <summary>What the pad is, as far as the host could tell when it connected (<see cref="GamepadType.Unknown"/> if
	/// it couldn't): for prompts that name its buttons.</summary>
	public GamepadType Type { get; }

	/// <summary>The family whose labels the pad's face buttons carry.</summary>
	public GamepadFamily Family => Type.Family();

	/// <summary>The pad's USB vendor id, or 0 where the host can't say.</summary>
	public ushort Vendor => (ushort)(_ids >> 16);

	/// <summary>The pad's USB product id, or 0 where the host can't say.</summary>
	public ushort Product => (ushort)_ids;

	/// <summary>True on the frame the pad became connected.</summary>
	public bool JustConnected => Connected && !_prevConnected;

	/// <summary>True on the frame the pad became disconnected.</summary>
	public bool JustDisconnected => !Connected && _prevConnected;

	/// <summary>The heavy/left analog trigger, [0, 1].</summary>
	public float LeftTrigger => _leftTrigger;

	/// <summary>The right analog trigger, [0, 1].</summary>
	public float RightTrigger => _rightTrigger;

	/// <summary>Whether <paramref name="button"/> is currently held.</summary>
	public bool Down(GamepadButton button) => (_buttons & Bit(button)) != 0;

	/// <summary>Whether <paramref name="button"/> transitioned to held this frame.</summary>
	public bool Pressed(GamepadButton button) => (_buttons & Bit(button)) != 0 && (_prevButtons & Bit(button)) == 0;

	/// <summary>Whether <paramref name="button"/> transitioned to released this frame.</summary>
	public bool Released(GamepadButton button) => (_buttons & Bit(button)) == 0 && (_prevButtons & Bit(button)) != 0;

	/// <summary>The current level of <paramref name="axis"/> (stick axes [-1, 1], triggers [0, 1]).</summary>
	public float Axis(GamepadAxis axis) => axis switch
	{
		GamepadAxis.LeftX => _leftX,
		GamepadAxis.LeftY => _leftY,
		GamepadAxis.RightX => _rightX,
		GamepadAxis.RightY => _rightY,
		GamepadAxis.LeftTrigger => _leftTrigger,
		GamepadAxis.RightTrigger => _rightTrigger,
		_ => 0f,
	};

	private static uint Bit(GamepadButton button) => 1u << (int)button;
}
