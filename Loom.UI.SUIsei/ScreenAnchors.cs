using System.Numerics;

using Loom.Core.Geometry;
using Loom.Core.Hosting;
using SUIsei;
using SUIsei.Controls;

namespace Loom.UI.SUIsei;

/// <summary>
/// Pins SUIsei elements to points in the world: each frame, every anchored element moves to where its point projects
/// on screen, and fades out while the point is off-screen or behind the eye. Labels over objects, markers along a path,
/// a name over a character — UI that lives in screen space but follows the world.
/// <para>
/// Each element is wrapped in a container of its own. The container carries the anchor's position, as its
/// <see cref="UIElement.Translation"/>, and its fade, as its <see cref="UIElement.Opacity"/>, so the element's own layout,
/// transform and opacity animations are left alone and compose with them. Moving a container through
/// <c>Translation</c> writes its visual directly, so a frame of moving anchors lays nothing out. A container that has
/// faded out leaves layout and composition until its point returns, so hidden anchors cost nothing per frame.
/// </para>
/// <para>
/// <b>The anchors take a <see cref="UIView"/> of their own</b> — a root of its own on the HUD's host, or a host of its
/// own — and fill it. SUIsei recomposites a view's whole draw list whenever anything in it changes, and moving anchors
/// change every frame, so a view shared with a static HUD would recomposite that HUD every frame too. Draw it like any
/// view: <c>painter.Draw(ctx, anchors.View.Surface)</c>.
/// </para>
/// <para>
/// Each frame, between the host's <c>Tick</c> (which lays the elements out) and its <c>Render</c> (which composites
/// them), call <see cref="Update"/> with the view-projection the frame renders the world with. <see cref="Update"/>
/// allocates nothing; an anchor allocates its container once, in <see cref="Add"/>.
/// </para>
/// <para>
/// The scene does not occlude anchors: one whose point is behind a wall still shows, because UI draws over the frame
/// without reading its depth.
/// </para>
/// </summary>
public sealed class ScreenAnchors
{
	private readonly Matrix4x4 _clipSpaceCorrection;
	private readonly Canvas _root = new();
	private Slot[] _slots = new Slot[8];
	private int _used;      // high-water mark of _slots: every slot below it is live or on the free list
	private int _free = -1; // head of the free list, threaded through Slot.NextFree

	private struct Slot
	{
		public Border? Container; // null while the slot is free
		public Vector3 World;
		public Vector2 Alignment;
		public Vector2 PixelOffset;
		public float Fade;        // the container's opacity: 0 hidden … 1 shown
		public bool Collapsed;    // faded out, and out of layout and composition
		public int Generation;
		public int NextFree;
	}

	private ScreenAnchors(UIView view, in Matrix4x4 clipSpaceCorrection)
	{
		View = view;
		_clipSpaceCorrection = clipSpaceCorrection;
		view.Root.Content = _root;
	}

	/// <summary>
	/// Create anchors on <paramref name="view"/>, which they fill: its root's content must be empty. The engine's
	/// clip-space convention is read once here (<see cref="IEngineClipSpace.ClipSpaceCorrection"/>), so the
	/// view-projections passed to <see cref="Update"/> must have that correction composed in, as the ones an app hands
	/// its shaders do.
	/// </summary>
	public static ScreenAnchors Create(IEngineClipSpace engine, UIView view)
	{
		ArgumentNullException.ThrowIfNull(engine);
		ArgumentNullException.ThrowIfNull(view);
		if (view.Root.Content is not null)
			throw new ArgumentException(
				"ScreenAnchors fill their view, and this view already has content. Give the anchors a view of their own, " +
				"so their every-frame changes do not recomposite other UI.", nameof(view));
		return new ScreenAnchors(view, engine.ClipSpaceCorrection);
	}

	/// <summary>The view the anchors fill. Draw its surface from a pass to show them.</summary>
	public UIView View { get; }

	/// <summary>How long an anchor takes to fade fully in or out, in seconds. Default 0.2; zero shows and hides at once.</summary>
	public float FadeSeconds
	{
		get;
		set => field = value >= 0f ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "A fade cannot take negative time.");
	} = 0.2f;

	/// <summary>
	/// How far, in layout units, an anchor's point may leave the viewport before the anchor fades out. Default 0: it
	/// fades as soon as the point leaves. A positive margin keeps an element that straddles the edge shown for longer.
	/// </summary>
	public float ViewportMargin { get; set; }

	/// <summary>How many elements are anchored.</summary>
	public int Count { get; private set; }

	/// <summary>
	/// Anchor <paramref name="element"/> at <paramref name="world"/>. It joins hidden, and fades in from the first
	/// <see cref="Update"/> after the host has laid it out with its point on screen.
	/// </summary>
	/// <param name="element">The element to place. It must not already have a parent.</param>
	/// <param name="world">The world-space point the element follows.</param>
	/// <param name="alignment">Which point of the element sits on the anchor, as a fraction of its size: (0,0) its
	/// top-left corner, (0.5,0.5) its centre, (0.5,1) the middle of its bottom edge.</param>
	/// <param name="pixelOffset">A further offset from the projected point, in layout units (pixels at a layout scale of
	/// 1), applied after the alignment — to lift a label clear of the point it names.</param>
	/// <returns>The anchor's id, for <see cref="SetWorldPosition"/>, <see cref="SetPlacement"/> and
	/// <see cref="Remove"/>.</returns>
	public ScreenAnchorId Add(UIElement element, Vector3 world, Vector2 alignment, Vector2 pixelOffset = default)
	{
		ArgumentNullException.ThrowIfNull(element);
		var container = new Border { Child = element, Opacity = 0f, Visibility = Visibility.Collapsed };

		int index;
		if (_free >= 0)
		{
			index = _free;
			_free = _slots[index].NextFree;
		}
		else
		{
			if (_used == _slots.Length)
				Array.Resize(ref _slots, _slots.Length * 2);
			index = _used++;
		}

		ref var slot = ref _slots[index];
		slot.Container = container;
		slot.World = world;
		slot.Alignment = alignment;
		slot.PixelOffset = pixelOffset;
		slot.Fade = 0f;
		slot.Collapsed = true;
		slot.NextFree = -1;
		_root.Children.Add(container);
		Count++;
		return new ScreenAnchorId(index, slot.Generation);
	}

	/// <summary>Move an anchor's point. The element follows at the next <see cref="Update"/>.</summary>
	public void SetWorldPosition(ScreenAnchorId id, Vector3 world) => Live(id).World = world;

	/// <summary>Change which point of the element sits on the anchor, and the offset from it (see <see cref="Add"/>).</summary>
	public void SetPlacement(ScreenAnchorId id, Vector2 alignment, Vector2 pixelOffset)
	{
		ref var slot = ref Live(id);
		slot.Alignment = alignment;
		slot.PixelOffset = pixelOffset;
	}

	/// <summary>How far an anchor has faded in: 0 hidden, 1 fully shown. This is its container's opacity.</summary>
	public float GetFade(ScreenAnchorId id) => Live(id).Fade;

	/// <summary>
	/// Remove an anchor at once, without a fade, and take its element out of its container so it can be placed
	/// elsewhere. The id is dead afterwards: passing it again throws.
	/// </summary>
	public void Remove(ScreenAnchorId id)
	{
		ref var slot = ref Live(id);
		var container = slot.Container!;
		var children = _root.Children;
		for (var i = children.Count - 1; i >= 0; i--)
		{
			if (ReferenceEquals(children[i], container))
			{
				children.RemoveAt(i);
				break;
			}
		}
		container.Child = null;
		slot.Container = null;
		slot.Generation++;
		slot.NextFree = _free;
		_free = id.Index;
		Count--;
	}

	/// <summary>
	/// Place every anchor for this frame: project its point through <paramref name="viewProjection"/>, move its
	/// container there, and step its fade towards shown or hidden. Call between the host's <c>Tick</c> and its
	/// <c>Render</c>, so the elements' sizes are this frame's and the composite sees where they went.
	/// </summary>
	/// <param name="viewProjection">The view-projection this frame renders the world with, clip-space correction
	/// included.</param>
	/// <param name="framebufferSize">The size in pixels of the target the view-projection maps onto, and the view
	/// covers.</param>
	/// <param name="deltaSeconds">Time since the last update, which advances the fades.</param>
	public void Update(in Matrix4x4 viewProjection, Vector2 framebufferSize, double deltaSeconds)
	{
		var step = FadeSeconds > 0f ? (float)(deltaSeconds / FadeSeconds) : 1f;
		var layoutScale = View.Layer.LayoutScale;
		var viewport = framebufferSize / layoutScale;
		var margin = ViewportMargin;
		for (var i = 0; i < _used; i++)
		{
			ref var slot = ref _slots[i];
			if (slot.Container is not { } container)
				continue;

			// Behind the eye a projection means nothing: the container stays where it was last seen and fades out there.
			var inFront = viewProjection.TryProjectToScreen(slot.World, framebufferSize, _clipSpaceCorrection, out var screen, out _);
			var point = screen / layoutScale;
			var onScreen = inFront
				&& point.X >= -margin && point.Y >= -margin
				&& point.X <= viewport.X + margin && point.Y <= viewport.Y + margin;

			if (slot.Collapsed)
			{
				if (!onScreen)
					continue;
				// Back into layout. A collapsed container is never measured, so its size is unknown until the host's
				// next Tick lays it out: it stays transparent until then, and moves and fades in from the next update.
				container.Visibility = Visibility.Visible;
				slot.Collapsed = false;
				continue;
			}

			if (inFront)
			{
				var size = container.DesiredSize;
				var topLeft = point - slot.Alignment * new Vector2(size.Width, size.Height) + slot.PixelOffset;
				container.Translation = new Vector3(topLeft, 0f);
			}

			var fade = onScreen ? MathF.Min(1f, slot.Fade + step) : MathF.Max(0f, slot.Fade - step);
			if (fade != slot.Fade)
			{
				slot.Fade = fade;
				container.Opacity = fade;
			}
			if (fade <= 0f)
			{
				container.Visibility = Visibility.Collapsed;
				slot.Collapsed = true;
			}
		}
	}

	private ref Slot Live(ScreenAnchorId id)
	{
		if ((uint)id.Index >= (uint)_used || _slots[id.Index].Container is null || _slots[id.Index].Generation != id.Generation)
			throw new ArgumentException($"{id} does not name a live anchor of these ScreenAnchors: it was removed, or came from others.", nameof(id));
		return ref _slots[id.Index];
	}
}
