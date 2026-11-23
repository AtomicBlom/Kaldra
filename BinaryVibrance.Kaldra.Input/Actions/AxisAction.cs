namespace BinaryVibrance.Kaldra.Input;

/// <summary>
/// A typed handle to a scalar (1-D) axis action defined on an <see cref="InputActions"/> set. The handle is an
/// index, so per-frame lookup of the action's resolved value is O(1).
/// </summary>
public readonly record struct AxisAction(int Id);
