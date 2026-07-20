namespace Loom.Input;

/// <summary>The serializable bindings for a single named action.</summary>
public sealed class ActionBindings
{
	/// <summary>The action's name; matched to a defined action on load.</summary>
	public string Name { get; set; } = "";

	/// <summary>The sources bound to the action.</summary>
	public List<InputBinding> Sources { get; set; } = [];

	/// <summary>The deadzone applied to analog sources.</summary>
	public Deadzone Deadzone { get; set; } = Deadzone.None;

	/// <summary>The response curve applied to the resolved analog value.</summary>
	public ResponseCurve Curve { get; set; } = ResponseCurve.Linear;
}
