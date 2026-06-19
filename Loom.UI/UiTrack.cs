namespace Loom.UI;

// MU0 Phase A scaffold (PlanningUI.md). This assembly is the backend-neutral UI runtime. The neutral draw-data
// contract (UiVertex / UiDrawData / UiSurfaceId) and the EnableUI / DrawUI / FrameInputs.UiSurfaces seam live in
// Loom.Core; the runtime object model lands here from MU0 Phase B onward:
//   - dependency-object model + value-precedence ladder (animation > local > style > inherited > default),
//   - UIElement / FrameworkElement + controls (Grid / StackPanel / Canvas / Border / TextBlock / Button / ...),
//   - two-pass measure/arrange layout,
//   - the composition Visual tree + IUiCompositor + the default DrawListCompositor,
//   - routed events, focus / XYFocus, 2D + ray hit-testing, UiHost / UiView / UiRoot,
//   - animation / storyboards, and the MSDF TextLayoutEngine + FontAtlas.
// It references Loom.Core + Loom.Assets only and reaches the GPU solely through the neutral seams.
