namespace Loom.UI.Controls;

/// <summary>
/// A clickable <see cref="ContentControl"/> with a default background (PlanningUI.md §D2.1). Phase B exposes a
/// plain <see cref="Click"/> event; the routed <c>Click</c> event synthesized from pointer press/release lands
/// with the input pipeline in MU2.
/// </summary>
public sealed class Button : ContentControl
{
	public Button() => Background = HdrColor.FromRgb(60, 60, 70);

	public event EventHandler? Click;

	/// <summary>Raise <see cref="Click"/> (invoked by the input pipeline in MU2; public for headless tests now).</summary>
	public void PerformClick() => Click?.Invoke(this, EventArgs.Empty);
}
