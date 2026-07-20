namespace Loom.Input;

/// <summary>
/// A typed handle to a 2-D vector action defined on an <see cref="InputActions"/> set. The handle is an index,
/// so per-frame lookup of the action's resolved vector is O(1).
/// </summary>
public readonly record struct VectorAction(int Id);
