using System.Numerics;

namespace BinaryVibrance.Kaldra.Input;

/// <summary>
/// A LIFO stack of input layers whose position <i>is</i> its priority — there is no ordering key, band, or
/// per-layer enum. <see cref="Push"/> adds a layer on top; the top is processed first each frame and can block
/// input from everything beneath it. The app assembles the stack bottom-up in the order it wants (gameplay
/// first, then UI glue, then transient modals); whoever pushes a layer keeps the reference and removes it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Resolve"/> folds a frame's raw events down the stack over two ping-pong buffers. Each layer keeps
/// its <b>own</b> held-state snapshot advanced by its <b>own</b> surviving stream — never one global snapshot —
/// so a key a layer still holds is not lost just because a lower layer is blocked, and an injected key becomes
/// held only in the layers that receive it. When a layer engages a block on a held device the fold synthesizes
/// release edges into the lower streams, and re-press edges when it lifts, so nothing is left stuck or phantom.
/// </para>
/// <para>
/// The steady path allocates nothing: the buffers, the per-layer snapshots, and the scratch are preallocated and
/// reused. Structural changes requested from inside <see cref="IInputLayer.Process"/> take effect next frame, and
/// the event capacity fails loud on overflow rather than silently dropping input.
/// </para>
/// </remarks>
public sealed class InputLayerStack
{
	private readonly List<LayerEntry> _entries = []; // bottom → top (last is top)
	private LayerEntry[] _order = []; // top → bottom scratch, rebuilt each Resolve
	private readonly RumbleQueue _rumble = new();
	private readonly InputSnapshot _output;

	private readonly int _capacity;
	private readonly InputEvent[] _bufferA;
	private readonly InputEvent[] _bufferB;
	private readonly InputEvent[] _injected;
	private readonly InputEvent[] _consumed;
	private readonly bool[] _consumedUsed;

	// Accumulators for the layer currently being processed; reset before each Process call.
	private InputDevice _currentBlock;
	private int _injectedCount;
	private int _consumedCount;

	/// <summary>Creates a stack whose fold handles up to <paramref name="eventCapacity"/> events in a frame.</summary>
	public InputLayerStack(int eventCapacity = 1024)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(eventCapacity, 1);
		_capacity = eventCapacity;
		_bufferA = new InputEvent[eventCapacity];
		_bufferB = new InputEvent[eventCapacity];
		_injected = new InputEvent[eventCapacity];
		_consumed = new InputEvent[eventCapacity];
		_consumedUsed = new bool[eventCapacity];
		_output = new InputSnapshot(_rumble);
	}

	/// <summary>The number of layers in the stack.</summary>
	public int Count => _entries.Count;

	/// <summary>The bottom-of-stack resolved snapshot the app polls — valid after each <see cref="Resolve"/>.</summary>
	public InputSnapshot Output => _output;

	/// <summary>The queue rumble effects are enqueued onto (via <see cref="InputSnapshot.Rumble"/>), for a host adapter to drain.</summary>
	public RumbleQueue RumbleQueue => _rumble;

	/// <summary>
	/// Whether a layer asked for text input during the last <see cref="Resolve"/> (see
	/// <see cref="InputContext.RequestTextInput"/>): something is ready to take typed text, so a host whose keyboard is
	/// on the screen should show it. Asked again every frame, so it lapses the frame nothing asks.
	/// </summary>
	public bool TextInputRequested { get; private set; }

	/// <summary>Adds a layer on top of the stack, where it is processed first.</summary>
	public void Push(IInputLayer layer)
	{
		ArgumentNullException.ThrowIfNull(layer);
		if (Contains(layer))
			throw new InvalidOperationException("The layer is already in the stack.");

		_entries.Add(new LayerEntry(layer, _rumble));
	}

	/// <summary>Removes and returns the top layer, or null if the stack is empty.</summary>
	public IInputLayer? Pop()
	{
		if (_entries.Count == 0)
			return null;

		var entry = _entries[^1];
		_entries.RemoveAt(_entries.Count - 1);
		return entry.Layer;
	}

	/// <summary>Removes a specific layer from anywhere in the stack; returns whether it was present.</summary>
	public bool Remove(IInputLayer layer)
	{
		for (var i = 0; i < _entries.Count; i++)
		{
			if (ReferenceEquals(_entries[i].Layer, layer))
			{
				_entries.RemoveAt(i);
				return true;
			}
		}

		return false;
	}

	/// <summary>Whether <paramref name="layer"/> is in the stack.</summary>
	public bool Contains(IInputLayer layer)
	{
		foreach (var entry in _entries)
		{
			if (ReferenceEquals(entry.Layer, layer))
				return true;
		}

		return false;
	}

	/// <summary>
	/// Folds <paramref name="raw"/> down the stack for this frame and returns the bottom-of-stack resolved
	/// snapshot (also available as <see cref="Output"/>). With no layers this simply advances the output
	/// snapshot by the raw events.
	/// </summary>
	public InputSnapshot Resolve(ReadOnlySpan<InputEvent> raw)
	{
		if (raw.Length > _capacity)
			throw new InvalidOperationException($"Frame produced {raw.Length} input events, exceeding the stack capacity of {_capacity}.");

		TextInputRequested = false;
		var count = _entries.Count;
		if (_order.Length < count)
			_order = new LayerEntry[count];
		for (var k = 0; k < count; k++)
			_order[k] = _entries[count - 1 - k]; // top → bottom

		// Release leftover slots from a previously taller stack so popped/removed layers aren't kept alive by the
		// grow-only scratch. Costs nothing (no allocation) and is skipped when the stack hasn't shrunk.
		if (_order.Length > count)
			Array.Clear(_order, count, _order.Length - count);

		raw.CopyTo(_bufferA);
		var current = _bufferA;
		var other = _bufferB;
		var currentCount = raw.Length;

		for (var i = 0; i < count; i++)
		{
			var entry = _order[i];
			var incoming = current.AsSpan(0, currentCount);
			entry.Snapshot.Advance(incoming);

			// The snapshot that receives this layer's downstream — still holding last frame's state, because it
			// is advanced only when the fold reaches it (or, for the bottom, after the loop).
			var downstream = i < count - 1 ? _order[i + 1].Snapshot : _output;

			_currentBlock = InputDevice.None;
			_injectedCount = 0;
			_consumedCount = 0;
			var ctx = new InputContext(this, incoming, entry.Snapshot);
			entry.Layer.Process(ref ctx);
			var blockedNow = _currentBlock;

			var outCount = 0;
			for (var c = 0; c < _consumedCount; c++)
				_consumedUsed[c] = false;

			// Pass through the incoming events that survive this layer's consume + block filters.
			for (var e = 0; e < currentCount; e++)
			{
				ref readonly var ev = ref current[e];
				if (IsConsumed(in ev))
					continue;
				if ((ev.Device & blockedNow) != InputDevice.None)
					continue;
				Put(other, ref outCount, in ev);
			}

			outCount = SynthesizeBlockEdges(other, outCount, blockedNow, entry.BlockedLastFrame, entry.Snapshot, downstream);

			for (var j = 0; j < _injectedCount; j++)
				Put(other, ref outCount, in _injected[j]);

			entry.BlockedLastFrame = blockedNow;

			(current, other) = (other, current);
			currentCount = outCount;
		}

		_output.Advance(current.AsSpan(0, currentCount));
		return _output;
	}

	internal void BlockCurrent(InputDevice devices) => _currentBlock |= devices;

	internal void RequestTextInputCurrent() => TextInputRequested = true;

	internal void ConsumeCurrent(in InputEvent e)
	{
		if (_consumedCount >= _capacity)
			throw new InvalidOperationException($"A layer consumed more than the stack capacity of {_capacity} events.");
		_consumed[_consumedCount++] = e;
	}

	internal void InjectCurrent(in InputEvent e)
	{
		if (_injectedCount >= _capacity)
			throw new InvalidOperationException($"A layer injected more than the stack capacity of {_capacity} events.");
		_injected[_injectedCount++] = e;
	}

	private bool IsConsumed(in InputEvent e)
	{
		for (var c = 0; c < _consumedCount; c++)
		{
			if (!_consumedUsed[c] && _consumed[c] == e)
			{
				_consumedUsed[c] = true;
				return true;
			}
		}

		return false;
	}

	// When a block engages on a held device, release what the downstream still holds; when it lifts, re-press
	// what this layer holds that the downstream is missing. Covers the button-like devices that carry edges.
	private int SynthesizeBlockEdges(InputEvent[] buffer, int outCount, InputDevice blockedNow, InputDevice blockedBefore,
		InputSnapshot layer, InputSnapshot downstream)
	{
		outCount = SynthesizeDevice(buffer, outCount, InputDevice.Keyboard, blockedNow, blockedBefore, layer, downstream, EdgeDevice.Keyboard);
		outCount = SynthesizeDevice(buffer, outCount, InputDevice.Mouse, blockedNow, blockedBefore, layer, downstream, EdgeDevice.Mouse);
		outCount = SynthesizeDevice(buffer, outCount, InputDevice.Gamepad, blockedNow, blockedBefore, layer, downstream, EdgeDevice.Gamepad);
		return outCount;
	}

	private int SynthesizeDevice(InputEvent[] buffer, int outCount, InputDevice device, InputDevice blockedNow,
		InputDevice blockedBefore, InputSnapshot layer, InputSnapshot downstream, EdgeDevice edge)
	{
		var engaged = (blockedNow & device) != InputDevice.None && (blockedBefore & device) == InputDevice.None;
		var disengaged = (blockedNow & device) == InputDevice.None && (blockedBefore & device) != InputDevice.None;
		if (!engaged && !disengaged)
			return outCount;

		switch (edge)
		{
			case EdgeDevice.Keyboard:
				for (var w = 0; w < InputSnapshot.KeyWordCount; w++)
				{
					// Engage: release the keys the downstream still holds. Disengage: press the keys this layer
					// holds that the downstream is missing.
					var bits = engaged ? downstream.KeyWordAt(w) : layer.KeyWordAt(w) & ~downstream.KeyWordAt(w);
					while (bits != 0)
					{
						var b = BitOperations.TrailingZeroCount(bits);
						bits &= bits - 1;
						var key = (Key)((w << 6) + b);
						Put(buffer, ref outCount, engaged ? InputEvent.KeyUp(key) : InputEvent.KeyDown(key));
					}
				}

				break;

			case EdgeDevice.Mouse:
				var mouse = engaged ? downstream.MouseButtonBits : layer.MouseButtonBits & ~downstream.MouseButtonBits;
				while (mouse != 0)
				{
					var b = BitOperations.TrailingZeroCount(mouse);
					mouse &= mouse - 1;
					var button = (MouseButton)b;
					Put(buffer, ref outCount, engaged ? InputEvent.MouseButtonUp(button) : InputEvent.MouseButtonDown(button));
				}

				break;

			case EdgeDevice.Gamepad:
				for (var pad = 0; pad < InputSnapshot.PadSlots; pad++)
				{
					var pads = engaged ? downstream.PadButtonBits(pad) : layer.PadButtonBits(pad) & ~downstream.PadButtonBits(pad);
					while (pads != 0)
					{
						var b = BitOperations.TrailingZeroCount(pads);
						pads &= pads - 1;
						var button = (GamepadButton)b;
						Put(buffer, ref outCount, engaged ? InputEvent.GamepadButtonUp(pad, button) : InputEvent.GamepadButtonDown(pad, button));
					}
				}

				break;
		}

		return outCount;
	}

	private void Put(InputEvent[] buffer, ref int count, in InputEvent e)
	{
		if (count >= _capacity)
			throw new InvalidOperationException($"Resolving the layer stack exceeded the event capacity of {_capacity}.");
		buffer[count++] = e;
	}

	private enum EdgeDevice
	{
		Keyboard,
		Mouse,
		Gamepad,
	}
}
