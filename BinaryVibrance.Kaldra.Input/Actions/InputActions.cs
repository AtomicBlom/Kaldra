using System.Numerics;
using System.Text.Json;

namespace BinaryVibrance.Kaldra.Input;

/// <summary>
/// A set of named actions and the sources bound to them — defined once (like a graph), then resolved each frame
/// against an <see cref="InputSnapshot"/> so per-frame queries are an O(1) array lookup by typed handle. Bindings
/// can be changed at runtime (<see cref="Rebind"/>) and persisted as JSON (<see cref="SaveBindings"/> /
/// <see cref="LoadBindings"/>).
/// </summary>
/// <remarks>
/// Button edges are computed against the previous <see cref="Resolve"/>, so resolve exactly once per frame (an
/// <see cref="ActionMapLayer"/> does this inside the layer fold). A resolved value is valid for the whole frame.
/// </remarks>
public sealed class InputActions
{
	private readonly ActionDefinition[] _definitions;
	private readonly Dictionary<string, int> _byName;
	private readonly ActionState[] _states;
	// Button actions pressed from outside their bindings (Press), held for the next Resolve
	private readonly bool[] _pressedNext;
	private readonly bool[] _previousDown;

	internal InputActions(ActionDefinition[] definitions, Dictionary<string, int> byName)
	{
		_definitions = definitions;
		_byName = byName;
		_states = new ActionState[definitions.Length];
		_pressedNext = new bool[definitions.Length];
		_previousDown = new bool[definitions.Length];
		foreach (var definition in definitions)
			definition.KeepAsDefault();
	}

	/// <summary>The actions' names, in the order they were defined.</summary>
	public IEnumerable<string> Names => _definitions.Select(d => d.Name);

	/// <summary>Whether <paramref name="name"/> is a button, an axis or a vector action.</summary>
	public ActionKind KindOf(string name) => _definitions[IndexOf(name)].Kind;

	/// <summary>The sources bound to <paramref name="name"/> now (after any <see cref="Rebind"/> or <see cref="LoadBindings"/>).</summary>
	public IReadOnlyList<InputBinding> Bindings(string name) => _definitions[IndexOf(name)].Sources;

	/// <summary>The sources <paramref name="name"/> was defined with.</summary>
	public IReadOnlyList<InputBinding> Defaults(string name) => _definitions[IndexOf(name)].DefaultSources;

	/// <summary>Puts <paramref name="name"/>'s sources, deadzone and curve back as they were defined.</summary>
	public void ResetToDefaults(string name) => _definitions[IndexOf(name)].ResetToDefault();

	/// <summary>Puts every action's sources, deadzone and curve back as they were defined.</summary>
	public void ResetToDefaults()
	{
		foreach (var definition in _definitions)
			definition.ResetToDefault();
	}

	/// <summary>
	/// The actions that read <paramref name="control"/> in any of their sources (a composite's key counts), for warning of
	/// a binding that is taken. Pad controls match whatever pad index the binding names.
	/// </summary>
	public IEnumerable<string> ActionsUsing(InputControl control)
	{
		foreach (var definition in _definitions)
			if (definition.Sources.Any(s => s.Controls().Any(c => SameControl(c, control))))
				yield return definition.Name;
	}

	// One direction of an axis is the axis; a stick is both of its axes
	private static bool SameControl(InputControl a, InputControl b)
	{
		static (ControlKind, int)[] Parts(InputControl c) => c.Kind switch
		{
			ControlKind.PadAxis => [(ControlKind.PadAxis, c.Code)],
			ControlKind.PadStick => c.Code == 0
				? [(ControlKind.PadAxis, (int)GamepadAxis.LeftX), (ControlKind.PadAxis, (int)GamepadAxis.LeftY)]
				: [(ControlKind.PadAxis, (int)GamepadAxis.RightX), (ControlKind.PadAxis, (int)GamepadAxis.RightY)],
			ControlKind.PadTriggers => [(ControlKind.PadAxis, (int)GamepadAxis.LeftTrigger), (ControlKind.PadAxis, (int)GamepadAxis.RightTrigger)],
			_ => [(c.Kind, c.Code)],
		};
		return Parts(a).Intersect(Parts(b)).Any();
	}

	/// <summary>Defines an action set with the given declarations.</summary>
	public static InputActions Define(Action<InputActionsBuilder> build)
	{
		ArgumentNullException.ThrowIfNull(build);
		var builder = new InputActionsBuilder();
		build(builder);
		return builder.Build();
	}

	/// <summary>The number of actions in the set.</summary>
	public int Count => _definitions.Length;

	/// <summary>The button-action handle for <paramref name="name"/>.</summary>
	public ButtonAction Button(string name) => new(IndexOf(name, ActionKind.Button));

	/// <summary>The axis-action handle for <paramref name="name"/>.</summary>
	public AxisAction Axis(string name) => new(IndexOf(name, ActionKind.Axis));

	/// <summary>The vector-action handle for <paramref name="name"/>.</summary>
	public VectorAction Vector(string name) => new(IndexOf(name, ActionKind.Vector));

	/// <summary>The resolved state of a button action.</summary>
	public ActionState State(ButtonAction action) => _states[action.Id];

	/// <summary>Whether a button action is currently held.</summary>
	public bool Down(ButtonAction action) => _states[action.Id].Down;

	/// <summary>Whether a button action transitioned to held this frame.</summary>
	public bool Pressed(ButtonAction action) => _states[action.Id].Pressed;

	/// <summary>Whether a button action transitioned to released this frame.</summary>
	public bool Released(ButtonAction action) => _states[action.Id].Released;

	/// <summary>
	/// Presses <paramref name="action"/> from outside its bindings, as an on-screen button or a tap does: the next
	/// <see cref="Resolve"/> finds it held whatever its controls are doing, so it is pressed that frame (if it wasn't held
	/// already) and released the one after, like a key tapped. Its bindings are untouched, so rebinding it moves its
	/// controls and leaves this alone.
	/// </summary>
	public void Press(ButtonAction action) => _pressedNext[action.Id] = true;

	/// <summary>The resolved scalar of an axis action.</summary>
	public float Value(AxisAction action) => _states[action.Id].Value;

	/// <summary>The resolved vector of a vector action.</summary>
	public Vector2 Value(VectorAction action) => _states[action.Id].Vector;

	/// <summary>Replaces the sources bound to <paramref name="name"/> with <paramref name="sources"/>.</summary>
	public void Rebind(string name, params InputBinding[] sources)
	{
		var definition = _definitions[IndexOf(name)];
		definition.Sources = [.. sources];
	}

	/// <summary>Resolves every action against <paramref name="snapshot"/>, updating the states this set exposes.</summary>
	public void Resolve(InputSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		for (var i = 0; i < _definitions.Length; i++)
		{
			var definition = _definitions[i];
			_states[i] = definition.Kind switch
			{
				ActionKind.Button => ResolveButton(i, definition, snapshot),
				ActionKind.Axis => new ActionState { Value = ResolveAxis(definition, snapshot) },
				ActionKind.Vector => new ActionState { Vector = ResolveVector(definition, snapshot) },
				_ => default,
			};
		}
	}

	/// <summary>Serializes the current bindings (including any rebindings) to JSON.</summary>
	public string SaveBindings()
	{
		var document = new BindingsDocument();
		foreach (var definition in _definitions)
		{
			document.Actions.Add(new ActionBindings
			{
				Name = definition.Name,
				Sources = [.. definition.Sources],
				Deadzone = definition.Deadzone,
				Curve = definition.Curve,
			});
		}

		return JsonSerializer.Serialize(document, InputBindingsJsonContext.Default.BindingsDocument);
	}

	/// <summary>
	/// Overlays saved bindings from <paramref name="json"/> onto this set, matching by action name. Actions the
	/// document does not mention keep their current bindings; names it mentions that this set does not define are
	/// ignored, so a saved file survives an action being renamed or removed.
	/// </summary>
	public void LoadBindings(string json)
	{
		ArgumentNullException.ThrowIfNull(json);
		var document = JsonSerializer.Deserialize(json, InputBindingsJsonContext.Default.BindingsDocument);
		if (document is null)
			return;

		foreach (var action in document.Actions)
		{
			if (!_byName.TryGetValue(action.Name, out var index))
				continue;

			var definition = _definitions[index];
			definition.Sources = [.. action.Sources];
			definition.Deadzone = action.Deadzone;
			definition.Curve = action.Curve;
		}
	}

	private ActionState ResolveButton(int index, ActionDefinition definition, InputSnapshot snapshot)
	{
		var down = false;
		foreach (var source in definition.Sources)
		{
			if (source.Kind == BindingKind.Button && IsButtonDown(source, snapshot))
			{
				down = true;
				break;
			}
		}

		if (_pressedNext[index])
		{
			down = true;
			_pressedNext[index] = false;
		}

		var previous = _previousDown[index];
		_previousDown[index] = down;
		return new ActionState
		{
			Down = down,
			Pressed = down && !previous,
			Released = !down && previous,
		};
	}

	private static float ResolveAxis(ActionDefinition definition, InputSnapshot snapshot)
	{
		var value = 0f;
		foreach (var source in definition.Sources)
			value += AxisContribution(source, definition.Deadzone, snapshot);

		return definition.Curve.Apply(value);
	}

	private static Vector2 ResolveVector(ActionDefinition definition, InputSnapshot snapshot)
	{
		var value = Vector2.Zero;
		foreach (var source in definition.Sources)
			value += VectorContribution(source, definition.Deadzone, snapshot);

		if (value.LengthSquared() > 1f)
			value = Vector2.Normalize(value);

		if (definition.Curve.Kind != ResponseCurveKind.Linear)
		{
			var magnitude = value.Length();
			if (magnitude > 0f)
				value *= definition.Curve.Apply(magnitude) / magnitude;
		}

		return value;
	}

	private static float AxisContribution(InputBinding source, Deadzone deadzone, InputSnapshot snapshot)
	{
		switch (source.Kind)
		{
			case BindingKind.Button:
				return IsButtonDown(source, snapshot) ? 1f : 0f;
			case BindingKind.Axis:
				return deadzone.Apply(snapshot.Gamepad(source.PadIndex).Axis((GamepadAxis)source.Code));
			case BindingKind.Triggers:
				var pad = snapshot.Gamepad(source.PadIndex);
				return deadzone.Apply(pad.RightTrigger - pad.LeftTrigger);
			case BindingKind.MouseWheelY:
				return snapshot.Wheel.Y;
			case BindingKind.MouseWheelX:
				return snapshot.Wheel.X;
			case BindingKind.Composite1D:
				var negative = IsButtonDown(source.Family, source.Code, source.PadIndex, snapshot) ? 1f : 0f;
				var positive = IsButtonDown(source.Family, source.Code2, source.PadIndex, snapshot) ? 1f : 0f;
				return positive - negative;
			default:
				return 0f;
		}
	}

	private static Vector2 VectorContribution(InputBinding source, Deadzone deadzone, InputSnapshot snapshot)
	{
		switch (source.Kind)
		{
			case BindingKind.Composite2D:
				var up = IsButtonDown(source.Family, source.Code, source.PadIndex, snapshot) ? 1f : 0f;
				var down = IsButtonDown(source.Family, source.Code2, source.PadIndex, snapshot) ? 1f : 0f;
				var left = IsButtonDown(source.Family, source.Code3, source.PadIndex, snapshot) ? 1f : 0f;
				var right = IsButtonDown(source.Family, source.Code4, source.PadIndex, snapshot) ? 1f : 0f;
				return new Vector2(right - left, up - down);
			case BindingKind.Stick:
				var pad = snapshot.Gamepad(source.PadIndex);
				var raw = new Vector2(pad.Axis((GamepadAxis)source.Code), pad.Axis((GamepadAxis)source.Code2));
				return deadzone.Apply(raw);
			default:
				return Vector2.Zero;
		}
	}

	private static bool IsButtonDown(InputBinding source, InputSnapshot snapshot)
		=> IsButtonDown(source.Family, source.Code, source.PadIndex, snapshot);

	private static bool IsButtonDown(InputFamily family, int code, int padIndex, InputSnapshot snapshot) => family switch
	{
		InputFamily.Keyboard => snapshot.Down((Key)code),
		InputFamily.Mouse => snapshot.Down((MouseButton)code),
		InputFamily.Gamepad => snapshot.Gamepad(padIndex).Down((GamepadButton)code),
		_ => false,
	};

	private int IndexOf(string name)
	{
		if (!_byName.TryGetValue(name, out var index))
			throw new KeyNotFoundException($"No action named '{name}' is defined.");
		return index;
	}

	private int IndexOf(string name, ActionKind expected)
	{
		var index = IndexOf(name);
		if (_definitions[index].Kind != expected)
			throw new InvalidOperationException($"Action '{name}' is a {_definitions[index].Kind} action, not a {expected} action.");
		return index;
	}
}
