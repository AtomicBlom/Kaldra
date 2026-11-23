namespace BinaryVibrance.Kaldra.Input;

/// <summary>
/// A layer's view of a frame during resolution, handed to <see cref="IInputLayer.Process"/>. It exposes the
/// events reaching this layer (<see cref="Incoming"/>, after everything higher blocked/consumed/injected) and
/// this layer's own held state (<see cref="Snapshot"/>), and lets the layer shape what lower layers see via
/// <see cref="Block"/>, <see cref="Consume"/>, and <see cref="Inject"/>. A <c>ref struct</c> so it cannot escape
/// the call or allocate.
/// </summary>
public ref struct InputContext
{
	private readonly InputLayerStack _stack;

	internal InputContext(InputLayerStack stack, ReadOnlySpan<InputEvent> incoming, InputSnapshot snapshot)
	{
		_stack = stack;
		Incoming = incoming;
		Snapshot = snapshot;
	}

	/// <summary>The events reaching this layer this frame, including text events.</summary>
	public ReadOnlySpan<InputEvent> Incoming { get; }

	/// <summary>This layer's own held state, already advanced by <see cref="Incoming"/> — poll it for level and
	/// edge queries (held keys, pointer delta, gamepad sticks).</summary>
	public InputSnapshot Snapshot { get; }

	/// <summary>
	/// Blocks whole device families from the layers beneath this one for the rest of the frame. Held inputs on a
	/// newly blocked device are released downstream automatically, and re-pressed when the block lifts, so no key
	/// is left stuck or phantom.
	/// </summary>
	public readonly void Block(InputDevice devices) => _stack.BlockCurrent(devices);

	/// <summary>Drops a single event from what lower layers see (matched by value).</summary>
	public readonly void Consume(in InputEvent e) => _stack.ConsumeCurrent(in e);

	/// <summary>Appends a synthetic event that lower layers receive as if it were real. Injected events are not
	/// subject to this layer's own <see cref="Block"/>, so a layer can block the real player yet drive the
	/// layers below with scripted input.</summary>
	public readonly void Inject(in InputEvent e) => _stack.InjectCurrent(in e);

	/// <summary>
	/// Says this layer is ready to take typed text this frame — a text field holds focus — so a host whose keyboard is
	/// on the screen shows it (<see cref="InputLayerStack.TextInputRequested"/>). Ask every frame it holds: the request
	/// lapses the first frame no layer makes it, and the host then hides the keyboard. A desktop host, whose keyboard
	/// is always there, ignores it.
	/// </summary>
	public readonly void RequestTextInput() => _stack.RequestTextInputCurrent();
}
