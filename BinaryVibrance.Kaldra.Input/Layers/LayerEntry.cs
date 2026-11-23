namespace BinaryVibrance.Kaldra.Input;

/// <summary>
/// The stack's persistent bookkeeping for one layer: the layer itself, its own held-state snapshot (advanced by
/// the events that survive higher layers), and which device families it blocked last frame (so the fold can
/// synthesize release/press edges the moment a block engages or lifts).
/// </summary>
internal sealed class LayerEntry
{
	public LayerEntry(IInputLayer layer, RumbleQueue rumble)
	{
		Layer = layer;
		Snapshot = new InputSnapshot(rumble);
	}

	public IInputLayer Layer { get; }

	public InputSnapshot Snapshot { get; }

	public InputDevice BlockedLastFrame { get; set; }
}
