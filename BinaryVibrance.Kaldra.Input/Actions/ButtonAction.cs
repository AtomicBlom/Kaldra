namespace BinaryVibrance.Kaldra.Input;

/// <summary>
/// A typed handle to a button action defined on an <see cref="InputActions"/> set. The handle is an index, so
/// per-frame lookup of the action's resolved state is O(1). Obtain one from <see cref="InputActionsBuilder"/>
/// while defining, or from <see cref="InputActions.Button(string)"/> by name afterwards.
/// </summary>
public readonly record struct ButtonAction(int Id);
