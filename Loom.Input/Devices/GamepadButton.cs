namespace Loom.Input;

/// <summary>
/// A neutral gamepad button, named by position rather than by any vendor's face labels so the mapping is
/// unambiguous across controllers: <see cref="South"/>/<see cref="East"/>/<see cref="West"/>/<see cref="North"/>
/// are the four face buttons (A/B/X/Y on an Xbox pad, Cross/Circle/Square/Triangle on a PlayStation pad). Values
/// are contiguous from zero so a button doubles as a bit index into the snapshot's held-button mask.
/// </summary>
public enum GamepadButton
{
	/// <summary>Bottom face button (Xbox A, PlayStation Cross).</summary>
	South = 0,

	/// <summary>Right face button (Xbox B, PlayStation Circle).</summary>
	East,

	/// <summary>Left face button (Xbox X, PlayStation Square).</summary>
	West,

	/// <summary>Top face button (Xbox Y, PlayStation Triangle).</summary>
	North,

	LeftBumper,
	RightBumper,

	/// <summary>The "view"/"select"/"share" button.</summary>
	Back,

	/// <summary>The "menu"/"options"/"start" button.</summary>
	Start,

	/// <summary>The central guide/home button.</summary>
	Guide,

	/// <summary>Pressing in the left stick (L3).</summary>
	LeftStick,

	/// <summary>Pressing in the right stick (R3).</summary>
	RightStick,

	DPadUp,
	DPadDown,
	DPadLeft,
	DPadRight,
}
