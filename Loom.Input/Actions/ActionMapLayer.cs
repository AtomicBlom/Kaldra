namespace Loom.Input;

/// <summary>
/// An <see cref="IInputLayer"/> that resolves an <see cref="InputActions"/> set against its own position in the
/// stack each frame — so the action states reflect exactly the input that survived the layers above it. It does
/// not block or consume; push UI-glue layers above it to arbitrate. Query the results through
/// <see cref="Actions"/> during the frame.
/// </summary>
public sealed class ActionMapLayer(InputActions actions) : IInputLayer
{
	/// <summary>The action set this layer resolves.</summary>
	public InputActions Actions { get; } = actions;

	/// <inheritdoc />
	public void Process(ref InputContext ctx) => Actions.Resolve(ctx.Snapshot);
}
