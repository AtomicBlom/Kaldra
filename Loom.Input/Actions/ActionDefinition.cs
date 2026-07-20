namespace Loom.Input;

/// <summary>
/// The mutable definition of one action while a set is being built and rebound: its name, kind, the sources
/// bound to it, and the deadzone/curve processors applied when resolving analog values. Rebinding and loading
/// saved bindings mutate this in place; per-frame resolution only reads it.
/// </summary>
internal sealed class ActionDefinition(string name, ActionKind kind)
{
	public string Name { get; } = name;

	public ActionKind Kind { get; } = kind;

	public List<InputBinding> Sources { get; set; } = [];

	public Deadzone Deadzone { get; set; } = Deadzone.None;

	public ResponseCurve Curve { get; set; } = ResponseCurve.Linear;
}
