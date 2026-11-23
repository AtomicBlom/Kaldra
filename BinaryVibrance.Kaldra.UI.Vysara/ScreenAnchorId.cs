namespace BinaryVibrance.Kaldra.UI.Vysara;

/// <summary>
/// Names one anchor of a <see cref="ScreenAnchors"/>. An id outlives its anchor only as a dead id: once the anchor is
/// removed, passing the id back throws, even after its slot holds a new anchor.
/// </summary>
/// <param name="Index">The anchor's slot.</param>
/// <param name="Generation">How many anchors that slot held before this one, which tells a dead id from a live one.</param>
public readonly record struct ScreenAnchorId(int Index, int Generation);
