using Loom.Core;

namespace Loom.UI;

/// <summary>The portability boundary (PlanningUI.md §D2.5): renders a surface's <see cref="RootVisual"/> tree.
/// Loom ships <see cref="DrawListCompositor"/>; a foreign engine substitutes its own and consumes the tree directly.</summary>
public interface IUICompositor
{
	/// <summary>Turn one surface's composition tree into rendering.</summary>
	void Composite(RootVisual root, UiSurfaceId surface);
}