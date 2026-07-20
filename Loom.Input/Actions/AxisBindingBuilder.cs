namespace Loom.Input;

/// <summary>
/// Fluent configuration returned by <see cref="InputActionsBuilder.Axis"/>, for attaching an optional
/// <see cref="Deadzone"/> and <see cref="ResponseCurve"/>. Converts implicitly to the <see cref="AxisAction"/>
/// handle, so a definition line can end by assigning the handle directly.
/// </summary>
public sealed class AxisBindingBuilder
{
	private readonly ActionDefinition _definition;

	internal AxisBindingBuilder(ActionDefinition definition, AxisAction action)
	{
		_definition = definition;
		Action = action;
	}

	/// <summary>The handle to the axis action being configured.</summary>
	public AxisAction Action { get; }

	/// <summary>Applies a deadzone to the action's analog sources.</summary>
	public AxisBindingBuilder Deadzone(Deadzone deadzone)
	{
		_definition.Deadzone = deadzone;
		return this;
	}

	/// <summary>Applies a response curve to the resolved value.</summary>
	public AxisBindingBuilder Curve(ResponseCurve curve)
	{
		_definition.Curve = curve;
		return this;
	}

	/// <summary>Yields the axis action handle.</summary>
	public static implicit operator AxisAction(AxisBindingBuilder builder) => builder.Action;
}
