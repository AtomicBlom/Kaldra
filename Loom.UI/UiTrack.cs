using System.Runtime.CompilerServices;

// MU0 Phase A/B scaffold (PlanningUI.md). This assembly is the backend-neutral UI runtime: the dependency-object
// model + value-precedence ladder, UIElement/FrameworkElement + two-pass layout, the controls, the composition
// Visual tree + IUiCompositor + the default DrawListCompositor, and UIRoot — all reaching the GPU only through the
// neutral Loom.Core seams. Still to come (MU1+): the source generator, MSDF text, input/focus, animation,
// in-world surfaces, and the UIHost / UIView host layer.

// Headless tests assert the precedence ladder's animation/style rungs and layout dirty flags, which are internal.
[assembly: InternalsVisibleTo("Loom.UI.Tests")]
