using System.Numerics;
using System.Text;

using Loom.Core;

namespace Loom.UI;

/// <summary>
/// A per-window UI host (PlanningUI.md §D5.1) — <b>not</b> a singleton, so multi-window/multi-surface is first-class.
/// It owns the input routing and the set of roots <i>outside</i> the render graph, holds <b>no</b> engine/backend
/// handle, and exposes the per-frame split the engine consumes: queue neutral events between frames, then once per
/// frame <see cref="Tick"/> (drain → hit-test → route → lay out) and <see cref="Render"/> (composite each view →
/// <see cref="Surfaces"/>), handed to <see cref="FrameInputs.UISurfaces"/>. Both run headless (no GPU), so the whole
/// pipeline is unit-tested by queuing events and asserting <see cref="Surfaces"/>/<see cref="DumpState"/> (§D8).
/// Focus + animation clocks slot in at MU3/MU4.
/// </summary>
public sealed class UIHost
{
	private readonly List<UIView> _views = [];
	private readonly Queue<PointerInput> _pointerQueue = new();
	private readonly List<PointerInput> _drain = [];
	private readonly InputManager _input = new();
	private readonly List<(UISurfaceId Id, UIDrawData Draw)> _surfaces = [];
	private int _nextSurface;
	private Size _framebufferSize = new(1f, 1f);

	/// <summary>Pointer routing + over-tracking + press-capture (persistent host state).</summary>
	public InputManager Input => _input;

	/// <summary>The roots this host owns.</summary>
	public IReadOnlyList<UIView> Views => _views;

	/// <summary>The last <see cref="Render"/> output — the surfaces handed to <see cref="FrameInputs.UISurfaces"/>.</summary>
	public IReadOnlyList<(UISurfaceId Id, UIDrawData Draw)> Surfaces => _surfaces;

	/// <summary>Add a root at <paramref name="placement"/>; returns the handle to keep. The first overlay gets
	/// <see cref="UISurface.Hud"/> (surface 0); subsequent views take higher ids.</summary>
	public UIView CreateRoot(in UIPlacement placement, bool interactive = true)
	{
		var root = new UIRoot(new UISurfaceId(_nextSurface++)) { LayoutScale = placement.LayoutScale };
		var view = new UIView(root, placement, interactive);
		_views.Add(view);
		return view;
	}

	public void RemoveRoot(UIView view) => _views.Remove(view);

	/// <summary>Queue a neutral pointer event for the next <see cref="Tick"/> (the host translates raw input to this).</summary>
	public void QueuePointer(in PointerInput e) => _pointerQueue.Enqueue(e);

	/// <summary>
	/// Once per frame, after queueing: lay out each root, then drain the pointer queue (coalescing consecutive moves
	/// per pointer) → hit-test over interactive views → route pointer events (bubble) + synthesize <c>Click</c>.
	/// Layout runs first so input hit-tests this frame's geometry (and works on the very first frame); input-driven
	/// changes recomposite via the dirty flags and lay out next frame. <paramref name="deltaSeconds"/>/
	/// <paramref name="time"/>/<paramref name="camera"/> are accepted now for signature stability (animation = MU4,
	/// the 3D ray = MU5).
	/// </summary>
	public void Tick(double deltaSeconds, float time, in Camera camera, Vector2 framebufferSize)
	{
		_framebufferSize = new Size(framebufferSize.X, framebufferSize.Y);
		foreach (var view in _views)
			view.Root.LayoutPass(_framebufferSize); // overlay = framebuffer; in-world extents arrive in MU5
		DrainPointers();
	}

	/// <summary>Composite each view's visual tree → <see cref="Surfaces"/> (PlanningUI.md §D5.1).</summary>
	public void Render()
	{
		_surfaces.Clear();
		foreach (var view in _views)
			_surfaces.Add((view.Surface, view.Root.Render()));
	}

	/// <summary>Focus / capture / over-chains for input + focus tests (PlanningUI.md §D8.1). Focus is MU3.</summary>
	public string DumpState()
	{
		var sb = new StringBuilder();
		sb.Append("focus=none\n");
		_input.DumpState(sb);
		return sb.ToString();
	}

	private void DrainPointers()
	{
		if (_pointerQueue.Count == 0)
			return;
		_drain.Clear();
		while (_pointerQueue.Count > 0)
			_drain.Add(_pointerQueue.Dequeue());

		for (var i = 0; i < _drain.Count; i++)
		{
			var e = _drain[i];
			if (e.Update == PointerUpdateKind.None && i + 1 < _drain.Count)
			{
				var n = _drain[i + 1];
				if (n.Update == PointerUpdateKind.None && n.PointerId == e.PointerId)
					continue; // coalesce a run of moves to the latest
			}
			_input.Process(e, _views);
		}
	}
}
