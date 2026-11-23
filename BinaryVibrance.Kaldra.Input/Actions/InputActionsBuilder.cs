namespace BinaryVibrance.Kaldra.Input;

/// <summary>
/// The declarative builder passed to <see cref="InputActions.Define"/>. Each <see cref="Button"/>/<see cref="Axis"/>/
/// <see cref="Vector"/> call names an action and lists the sources bound to it, returning a typed handle (axes and
/// vectors return a fluent builder for an optional deadzone/curve). Action names must be unique within a set.
/// </summary>
public sealed class InputActionsBuilder
{
	private readonly List<ActionDefinition> _definitions = [];
	private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);

	internal InputActionsBuilder()
	{
	}

	/// <summary>Defines a button action bound to <paramref name="sources"/> (held if any source is held).</summary>
	public ButtonAction Button(string name, params InputBinding[] sources)
		=> new(Add(name, ActionKind.Button, sources));

	/// <summary>Defines a scalar axis action bound to <paramref name="sources"/> (summed).</summary>
	public AxisBindingBuilder Axis(string name, params InputBinding[] sources)
	{
		var index = Add(name, ActionKind.Axis, sources);
		return new AxisBindingBuilder(_definitions[index], new AxisAction(index));
	}

	/// <summary>Defines a 2-D vector action bound to <paramref name="sources"/> (summed, clamped to unit length).</summary>
	public VectorBindingBuilder Vector(string name, params InputBinding[] sources)
	{
		var index = Add(name, ActionKind.Vector, sources);
		return new VectorBindingBuilder(_definitions[index], new VectorAction(index));
	}

	internal InputActions Build() => new([.. _definitions], _byName);

	private int Add(string name, ActionKind kind, InputBinding[] sources)
	{
		ArgumentException.ThrowIfNullOrEmpty(name);
		if (_byName.ContainsKey(name))
			throw new ArgumentException($"An action named '{name}' is already defined.", nameof(name));

		var definition = new ActionDefinition(name, kind) { Sources = [.. sources] };
		var index = _definitions.Count;
		_definitions.Add(definition);
		_byName[name] = index;
		return index;
	}
}
