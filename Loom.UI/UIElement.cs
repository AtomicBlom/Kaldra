using System.Numerics;
using Loom.Core.Primitives;

namespace Loom.UI;

/// <summary>
/// The base of every visual element (PlanningUI.md §D2.1): two-pass measure/arrange, layout invalidation, the
/// owned composition <see cref="Visual"/>, and the render-affecting dependency properties (<see cref="Opacity"/>,
/// <see cref="Visibility"/>, <see cref="IsHitTestVisible"/>). The WinUI <c>MeasureCore</c>/<c>ArrangeCore</c> seam
/// lets <see cref="FrameworkElement"/> layer Margin/Min/Max/alignment on top of the element's intrinsic sizing.
/// </summary>
public abstract class UIElement : DependencyObject
{
	public static readonly DependencyProperty<UIElement, float> OpacityProperty =
		DependencyProperty.Register<UIElement, float>(nameof(Opacity),
			new PropertyMetadata<UIElement, float> { DefaultValue = 1f, Invalidates = InvalidationFlags.Render });

	public static readonly DependencyProperty<UIElement, Visibility> VisibilityProperty =
		DependencyProperty.Register<UIElement, Visibility>(nameof(Visibility),
			new PropertyMetadata<UIElement, Visibility> { DefaultValue = Visibility.Visible, Invalidates = InvalidationFlags.Measure });

	public static readonly DependencyProperty<UIElement, bool> IsHitTestVisibleProperty =
		DependencyProperty.Register<UIElement, bool>(nameof(IsHitTestVisible),
			new PropertyMetadata<UIElement, bool> { DefaultValue = true });

	/// <summary>Group opacity (animatable — drives the composition <see cref="Visual.Opacity"/>).</summary>
	public float Opacity { get => GetValue(OpacityProperty); set => SetValue(OpacityProperty, value); }

	/// <summary><see cref="Visibility.Collapsed"/> contributes zero to layout and never hit-tests.</summary>
	public Visibility Visibility { get => GetValue(VisibilityProperty); set => SetValue(VisibilityProperty, value); }

	/// <summary>When false, this element (and its subtree, by the walk) is skipped by hit-testing.</summary>
	public bool IsHitTestVisible { get => GetValue(IsHitTestVisibleProperty); set => SetValue(IsHitTestVisibleProperty, value); }

	// --- Routed events (PlanningUI.md §D5.3). Pointer events bubble; enter/exit are direct (per-element). ---

	public static readonly RoutedEvent PointerPressedEvent = RoutedEvent.Register(nameof(PointerPressed), RoutingStrategy.Bubble);
	public static readonly RoutedEvent PointerReleasedEvent = RoutedEvent.Register(nameof(PointerReleased), RoutingStrategy.Bubble);
	public static readonly RoutedEvent PointerMovedEvent = RoutedEvent.Register(nameof(PointerMoved), RoutingStrategy.Bubble);
	public static readonly RoutedEvent PointerEnteredEvent = RoutedEvent.Register(nameof(PointerEntered), RoutingStrategy.Direct);
	public static readonly RoutedEvent PointerExitedEvent = RoutedEvent.Register(nameof(PointerExited), RoutingStrategy.Direct);

	public event RoutedEventHandler PointerPressed { add => AddHandler(PointerPressedEvent, value); remove => RemoveHandler(PointerPressedEvent, value); }
	public event RoutedEventHandler PointerReleased { add => AddHandler(PointerReleasedEvent, value); remove => RemoveHandler(PointerReleasedEvent, value); }
	public event RoutedEventHandler PointerMoved { add => AddHandler(PointerMovedEvent, value); remove => RemoveHandler(PointerMovedEvent, value); }
	public event RoutedEventHandler PointerEntered { add => AddHandler(PointerEnteredEvent, value); remove => RemoveHandler(PointerEnteredEvent, value); }
	public event RoutedEventHandler PointerExited { add => AddHandler(PointerExitedEvent, value); remove => RemoveHandler(PointerExitedEvent, value); }

	/// <summary>A local render transform, composed under layout placement (animatable later).</summary>
	public Matrix3x2 RenderTransform { get; set; } = Matrix3x2.Identity;

	/// <summary>The result of the last <see cref="Measure"/> (intrinsic + margin), in DIPs.</summary>
	public Size DesiredSize { get; protected set; }

	/// <summary>The final placement from the last <see cref="Arrange"/>, in parent space.</summary>
	public Rect LayoutRect { get; protected set; }

	internal bool MeasureDirty = true;
	internal bool ArrangeDirty = true;
	private Size? _lastAvailable;
	private Rect? _lastFinal;

	internal UIElement? Parent { get; set; }

	/// <summary>The composition node this element owns (PlanningUI.md §D2.1). Layout writes its transform/size;
	/// property setters write its opacity/brush. The retained visual tree mirrors the element tree.</summary>
	internal SpriteVisual Visual { get; } = new();

	/// <summary>The children that participate in layout + visual sync (panels return their children; content
	/// controls/borders return their single child; leaves return empty).</summary>
	internal virtual IReadOnlyList<UIElement> VisualChildren => [];

	// --- Two-pass layout (WinUI semantics) ---

	/// <summary>Pass 1: compute <see cref="DesiredSize"/> for the given available space. Cached when clean.</summary>
	public void Measure(Size available)
	{
		if (!MeasureDirty && _lastAvailable == available)
			return;
		_lastAvailable = available;
		DesiredSize = Visibility == Visibility.Collapsed ? Size.Zero : MeasureCore(available);
		MeasureDirty = false;
		ArrangeDirty = true;
	}

	/// <summary>Pass 2: place the element into <paramref name="finalRect"/> (parent space) and sync its visual.</summary>
	public void Arrange(Rect finalRect)
	{
		if (!ArrangeDirty && _lastFinal == finalRect)
			return;
		_lastFinal = finalRect;
		if (Visibility == Visibility.Collapsed)
			LayoutRect = new Rect(finalRect.X, finalRect.Y, 0, 0);
		else
			ArrangeCore(finalRect);
		ArrangeDirty = false;
		SyncVisual();
	}

	/// <summary>Intrinsic measure hook (Margin/Min/Max applied by <see cref="FrameworkElement"/> around this).</summary>
	protected virtual Size MeasureCore(Size available) => MeasureOverride(available);

	/// <summary>Intrinsic arrange hook — sets <see cref="LayoutRect"/> and arranges children.</summary>
	protected virtual void ArrangeCore(Rect finalRect)
	{
		LayoutRect = finalRect;
		ArrangeOverride(new Size(finalRect.Width, finalRect.Height));
	}

	/// <summary>Per-control measure: return the desired size for the content given <paramref name="available"/>.</summary>
	protected virtual Size MeasureOverride(Size available) => Size.Zero;

	/// <summary>Per-control arrange: place children within <paramref name="finalSize"/> (origin 0,0); return used size.</summary>
	protected virtual Size ArrangeOverride(Size finalSize) => finalSize;

	// --- Invalidation ---

	public void InvalidateMeasure()
	{
		if (MeasureDirty)
			return;
		MeasureDirty = true;
		ArrangeDirty = true;
		_lastAvailable = null;
		Parent?.InvalidateMeasure();
	}

	public void InvalidateArrange()
	{
		if (ArrangeDirty)
			return;
		ArrangeDirty = true;
		_lastFinal = null;
		Parent?.InvalidateArrange();
	}

	/// <summary>A render-only change (opacity, brush): refresh the composition node's render state and mark it dirty
	/// for the next composite — no relayout. Layout-driven sync happens in <see cref="SyncVisual"/> after arrange.</summary>
	public void InvalidateVisual()
	{
		Visual.Opacity = Opacity;
		Visual.MarkDirty();
	}

	/// <summary>Re-evaluate just this element's drawn content (a brush/background change) against the current
	/// <see cref="LayoutRect"/> — no relayout. Marks the visual dirty for the next composite.</summary>
	protected void InvalidateContent()
	{
		Visual.Content = BuildContent();
		Visual.MarkDirty();
	}

	protected override void OnPropertyInvalidated(DependencyProperty dp, InvalidationFlags flags)
	{
		if ((flags & InvalidationFlags.Measure) != 0)
			InvalidateMeasure();
		else if ((flags & InvalidationFlags.Arrange) != 0)
			InvalidateArrange();
		if ((flags & InvalidationFlags.Render) != 0)
			InvalidateVisual();
	}

	// --- Element -> composition sync (full rebuild in Phase B; incremental dirty-subtree is MU2) ---

	internal void SyncVisual()
	{
		Visual.Transform = RenderTransform * Matrix3x2.CreateTranslation(LayoutRect.X, LayoutRect.Y);
		Visual.Opacity = Opacity;
		Visual.Content = BuildContent();
		Visual.Clear();
		foreach (var child in VisualChildren)
			Visual.Add(child.Visual);
		Visual.MarkDirty();
	}

	/// <summary>The drawn content for this element's visual (a Border/Panel returns a <see cref="RectContent"/>;
	/// leaves without a background return null). Glyph/image content arrives with MSDF text + images (MU1).</summary>
	private protected virtual VisualContent? BuildContent() => null;

	// --- Routed-event handler store + dispatch (PlanningUI.md §D5.3) ---

	private Dictionary<ushort, RoutedEventHandler>? _handlers;

	[ThreadStatic] private static Stack<List<UIElement>>? _routePool;

	/// <summary>Attach a typed handler for <paramref name="evt"/> (no <c>MethodInfo</c>; a direct combined delegate).</summary>
	public void AddHandler(RoutedEvent evt, RoutedEventHandler handler)
	{
		_handlers ??= [];
		_handlers[evt.Id] = _handlers.TryGetValue(evt.Id, out var existing)
			? (RoutedEventHandler)Delegate.Combine(existing, handler)
			: handler;
	}

	/// <summary>Detach a handler previously added with <see cref="AddHandler"/>.</summary>
	public void RemoveHandler(RoutedEvent evt, RoutedEventHandler handler)
	{
		if (_handlers is null || !_handlers.TryGetValue(evt.Id, out var existing))
			return;
		var remaining = (RoutedEventHandler?)Delegate.Remove(existing, handler);
		if (remaining is null)
			_handlers.Remove(evt.Id);
		else
			_handlers[evt.Id] = remaining;
	}

	/// <summary>
	/// Raise <paramref name="evt"/> on this element as the original source and route it (PlanningUI.md §D5.3):
	/// tunnel root→target, bubble target→root, or direct. <see cref="RoutedEventArgs.Handled"/> short-circuits the
	/// route. The ancestor chain is built into a pooled, re-entrancy-safe buffer (a handler may raise another event).
	/// </summary>
	internal void RaiseEvent(RoutedEvent evt, RoutedEventArgs args)
	{
		args.Reset(evt, this);

		var pool = _routePool ??= new Stack<List<UIElement>>();
		var route = pool.Count > 0 ? pool.Pop() : new List<UIElement>(16);
		for (UIElement? e = this; e is not null; e = e.Parent)
			route.Add(e);

		switch (evt.Routing)
		{
			case RoutingStrategy.Tunnel:
				for (var i = route.Count - 1; i >= 0 && !args.Handled; i--)
					route[i].InvokeHandlers(args);
				break;
			case RoutingStrategy.Direct:
				InvokeHandlers(args);
				break;
			default: // Bubble
				for (var i = 0; i < route.Count && !args.Handled; i++)
					route[i].InvokeHandlers(args);
				break;
		}

		route.Clear();
		pool.Push(route);
	}

	private void InvokeHandlers(RoutedEventArgs args)
	{
		if (_handlers is null || args.RoutedEvent is null || !_handlers.TryGetValue(args.RoutedEvent.Id, out var h))
			return;
		args.Source = this;
		h(this, args);
	}

	// --- 2D hit-testing (PlanningUI.md §D5.4): reverse-z, respects IsHitTestVisible/Visibility/RenderTransform ---

	/// <summary>
	/// Return the top-most descendant (or this element) hit by <paramref name="point"/>, expressed in this element's
	/// local space (origin at this element's top-left, pre-<see cref="RenderTransform"/>). Children are tested
	/// front-to-back; an element is a hit only where it actually draws (its visual has content), so transparent
	/// panels let clicks fall through. Pure + side-effect-free — asserted directly in tests (§D8).
	/// </summary>
	internal UIElement? HitTest(Vector2 point)
	{
		if (!IsHitTestVisible || Visibility != Visibility.Visible)
			return null;

		var local = point;
		if (RenderTransform != Matrix3x2.Identity && Matrix3x2.Invert(RenderTransform, out var inv))
			local = Vector2.Transform(point, inv);

		var children = VisualChildren;
		for (var i = children.Count - 1; i >= 0; i--)
		{
			var c = children[i];
			var hit = c.HitTest(local - new Vector2(c.LayoutRect.X, c.LayoutRect.Y));
			if (hit is not null)
				return hit;
		}

		var bounds = new Rect(0f, 0f, LayoutRect.Width, LayoutRect.Height);
		return bounds.Contains(local) && Visual.Content is not null ? this : null;
	}

	internal void AdoptChild(UIElement child) => child.Parent = this;
	internal void DropChild(UIElement child) { if (child.Parent == this) child.Parent = null; }
}
