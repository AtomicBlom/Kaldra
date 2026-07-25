namespace Loom.Input;

/// <summary>The serializable form of a full set of action bindings — the payload of
/// <see cref="InputActions.SaveBindings"/> / <see cref="InputActions.LoadBindings"/>.</summary>
public sealed class BindingsDocument
{
	/// <summary>The per-action bindings.</summary>
	public List<ActionBindings> Actions { get; set; } = [];
}
