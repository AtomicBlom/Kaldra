using System.Linq;
using System.Numerics;
using System.Text;

namespace Loom.UI;

/// <summary>
/// Per-host pointer routing (PlanningUI.md §D5.1/§D5.4): hit-tests each queued <see cref="PointerInput"/> over the
/// host's interactive overlay views, raises the bubbling pointer routed events, tracks the "over" chain (so
/// enter/exit fire per element), and implicitly captures on press so a press→release pair routes to the same target
/// (the basis for <c>Button.Click</c>). Persistent host state, off the render graph; headless (no engine/backend, §D8).
/// Keyboard focus + explicit pointer capture (drag) arrive in MU3; the 3D ray path in MU5.
/// </summary>
public sealed class InputManager
{
	private readonly Dictionary<uint, List<UIElement>> _overChains = [];
	private readonly Dictionary<uint, UIElement?> _captured = [];
	private readonly PointerRoutedEventArgs _args = new();
	private List<UIElement> _scratch = [];

	/// <summary>Route one pointer event over <paramref name="views"/> (called by <see cref="UIHost.Tick"/>).</summary>
	internal void Process(in PointerInput e, IReadOnlyList<UIView> views)
	{
		var hit = HitTest(e.Position, views);
		UpdateOverChain(e, hit);

		if (IsPress(e.Update))
		{
			_captured[e.PointerId] = hit;
			if (hit is not null)
				Raise(UIElement.PointerPressedEvent, hit, e);
		}
		else if (IsRelease(e.Update))
		{
			var target = (_captured.TryGetValue(e.PointerId, out var cap) ? cap : null) ?? hit;
			if (target is not null)
				Raise(UIElement.PointerReleasedEvent, target, e);
			_captured.Remove(e.PointerId);
		}
		else if (hit is not null)
		{
			Raise(UIElement.PointerMovedEvent, hit, e);
		}
	}

	/// <summary>The element currently captured for <paramref name="pointerId"/> (implicit press-capture), or null.</summary>
	public UIElement? GetCapture(uint pointerId) => _captured.TryGetValue(pointerId, out var c) ? c : null;

	// Diff the new over-chain against the previous one and raise enter/exit (direct, per element). The previous
	// chain is reused as the next scratch buffer to avoid per-move allocation.
	private void UpdateOverChain(in PointerInput e, UIElement? hit)
	{
		_scratch.Clear();
		for (UIElement? el = hit; el is not null; el = el.Parent)
			_scratch.Add(el);

		var prev = _overChains.TryGetValue(e.PointerId, out var p) ? p : null;

		if (prev is not null)
			foreach (var el in prev)
				if (!Contains(_scratch, el))
					Raise(UIElement.PointerExitedEvent, el, e);

		foreach (var el in _scratch)
			if (prev is null || !Contains(prev, el))
				Raise(UIElement.PointerEnteredEvent, el, e);

		_overChains[e.PointerId] = _scratch;
		_scratch = prev ?? [];
	}

	private void Raise(RoutedEvent evt, UIElement target, in PointerInput e)
	{
		_args.Pointer = new PointerPoint(e.Position, e.Buttons, e.Pressure);
		_args.Device = e.Device;
		target.RaiseEvent(evt, _args);
	}

	// Surface priority (PlanningUI.md §D5.4): topmost screen layer wins. MU2 has overlays only.
	private static UIElement? HitTest(Vector2 pos, IReadOnlyList<UIView> views)
	{
		UIElement? best = null;
		var bestZ = int.MinValue;
		for (var i = 0; i < views.Count; i++)
		{
			var view = views[i];
			if (!view.Interactive || view.Placement.Kind != UIPlacementKind.Overlay)
				continue;
			var content = view.Root.Content;
			if (content is null)
				continue;
			var scale = view.Root.LayoutScale > 0f ? view.Root.LayoutScale : 1f;
			var dip = pos / scale;
			var local = dip - new Vector2(content.LayoutRect.X, content.LayoutRect.Y);
			var hit = content.HitTest(local);
			if (hit is not null && view.Placement.ZLayer >= bestZ)
			{
				best = hit;
				bestZ = view.Placement.ZLayer;
			}
		}
		return best;
	}

	private static bool Contains(List<UIElement> chain, UIElement el)
	{
		for (var i = 0; i < chain.Count; i++)
			if (ReferenceEquals(chain[i], el))
				return true;
		return false;
	}

	private static bool IsPress(PointerUpdateKind k) =>
		k is PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.RightButtonPressed or PointerUpdateKind.MiddleButtonPressed;

	private static bool IsRelease(PointerUpdateKind k) =>
		k is PointerUpdateKind.LeftButtonReleased or PointerUpdateKind.RightButtonReleased or PointerUpdateKind.MiddleButtonReleased;

	internal void DumpState(StringBuilder sb)
	{
		foreach (var id in _overChains.Keys.OrderBy(k => k))
		{
			var chain = _overChains[id];
			sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"pointer {id}: over=[");
			for (var i = 0; i < chain.Count; i++)
			{
				if (i > 0)
					sb.Append(", ");
				sb.Append(chain[i].GetType().Name);
			}
			var cap = _captured.TryGetValue(id, out var c) ? c : null;
			sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"] capture={cap?.GetType().Name ?? "none"}\n");
		}
	}
}
