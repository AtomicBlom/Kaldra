using System.Numerics;

namespace Loom.UI.Controls;

/// <summary>The ordered child collection of a <see cref="Panel"/>; adds re-parent the child and invalidate the
/// owner's measure (PlanningUI.md §D2.1).</summary>
public sealed class UIElementCollection
{
	private readonly List<UIElement> _items = [];
	private readonly Panel _owner;

	internal UIElementCollection(Panel owner) => _owner = owner;

	public int Count => _items.Count;
	public UIElement this[int index] => _items[index];
	internal IReadOnlyList<UIElement> Items => _items;

	public void Add(UIElement child)
	{
		_items.Add(child);
		_owner.AdoptChild(child);
		_owner.InvalidateMeasure();
	}

	public void Clear()
	{
		foreach (var c in _items)
			_owner.DropChild(c);
		_items.Clear();
		_owner.InvalidateMeasure();
	}
}

/// <summary>The base of multi-child layout containers (PlanningUI.md §D2.3): owns the <see cref="Children"/>
/// collection and an optional solid <see cref="Background"/>. Subclasses implement the measure/arrange policy.</summary>
public abstract class Panel : FrameworkElement
{
	protected Panel() => Children = new UIElementCollection(this);

	public UIElementCollection Children { get; }

	/// <summary>An optional solid fill behind the children (hit-testing makes a backgroundless panel transparent).</summary>
	public HdrColor? Background { get; set { if (field != value) { field = value; InvalidateContent(); } } }

	internal override IReadOnlyList<UIElement> VisualChildren => Children.Items;

	private protected override VisualContent? BuildContent() =>
		Background is { } bg ? new RectContent(new Vector2(LayoutRect.Width, LayoutRect.Height), bg) : null;
}
