namespace Loom.Input;

/// <summary>
/// One serializable source bound to an action: what device family it reads (<see cref="Family"/>), how to
/// interpret it (<see cref="BindingKind"/>), and up to four device codes plus a pad index. A single key,
/// button, or axis converts implicitly; multi-code sources (composites, sticks, triggers) come from the
/// <see cref="Pad"/>, <see cref="Mouse"/>, and <see cref="Composite"/> helpers. Being a flat value type with
/// no references, it round-trips cleanly through <see cref="InputActions.SaveBindings"/>.
/// </summary>
public readonly record struct InputBinding
{
	/// <summary>How the source is interpreted.</summary>
	public BindingKind Kind { get; init; }

	/// <summary>The device family the codes belong to.</summary>
	public InputFamily Family { get; init; }

	/// <summary>Primary code (key/button/axis, or composite "up"/"negative"/stick-X depending on <see cref="Kind"/>).</summary>
	public int Code { get; init; }

	/// <summary>Secondary code (composite "down"/"positive", or stick-Y).</summary>
	public int Code2 { get; init; }

	/// <summary>Tertiary code (composite "left").</summary>
	public int Code3 { get; init; }

	/// <summary>Quaternary code (composite "right").</summary>
	public int Code4 { get; init; }

	/// <summary>The gamepad index for gamepad-family sources.</summary>
	public int PadIndex { get; init; }

	/// <summary>Binds a keyboard key as a button.</summary>
	public static implicit operator InputBinding(Key key)
		=> new() { Kind = BindingKind.Button, Family = InputFamily.Keyboard, Code = (int)key };

	/// <summary>Binds a mouse button.</summary>
	public static implicit operator InputBinding(MouseButton button)
		=> new() { Kind = BindingKind.Button, Family = InputFamily.Mouse, Code = (int)button };

	/// <summary>Binds a gamepad button.</summary>
	public static implicit operator InputBinding(GamepadButton button)
		=> new() { Kind = BindingKind.Button, Family = InputFamily.Gamepad, Code = (int)button };

	/// <summary>Binds a gamepad analog axis.</summary>
	public static implicit operator InputBinding(GamepadAxis axis)
		=> new() { Kind = BindingKind.Axis, Family = InputFamily.Gamepad, Code = (int)axis };
}
