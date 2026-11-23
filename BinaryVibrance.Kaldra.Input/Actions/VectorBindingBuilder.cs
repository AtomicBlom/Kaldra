namespace BinaryVibrance.Kaldra.Input;

/// <summary>
/// Fluent configuration returned by <see cref="InputActionsBuilder.Vector"/>, for attaching an optional
/// <see cref="Deadzone"/> and <see cref="ResponseCurve"/>. Converts implicitly to the <see cref="VectorAction"/>
/// handle, so a definition line can end by assigning the handle directly.
/// </summary>
public sealed class VectorBindingBuilder
{
	private readonly ActionDefinition _definition;

	internal VectorBindingBuilder(ActionDefinition definition, VectorAction action)
	{
		_definition = definition;
		Action = action;
	}

	/// <summary>The handle to the vector action being configured.</summary>
	public VectorAction Action { get; }

	/// <summary>Applies a deadzone to the action's analog (stick) sources.</summary>
	public VectorBindingBuilder Deadzone(Deadzone deadzone)
	{
		_definition.Deadzone = deadzone;
		return this;
	}

	/// <summary>Applies a response curve to the resolved vector's magnitude.</summary>
	public VectorBindingBuilder Curve(ResponseCurve curve)
	{
		_definition.Curve = curve;
		return this;
	}

	/// <summary>Yields the vector action handle.</summary>
	public static implicit operator VectorAction(VectorBindingBuilder builder) => builder.Action;
}
