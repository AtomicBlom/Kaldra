namespace Loom.Core;

/// <summary>
/// Identifies one UI surface a frame may carry — the screen-space overlay and each in-world quad get a distinct id
/// (PlanningUI.md §Core surface additions). The host composites each surface to a <see cref="UIDrawData"/> and
/// hands the engine the set via <see cref="FrameInputs.UISurfaces"/>; a pass names one with
/// <see cref="IRenderContext.DrawUI"/>. Backend-neutral managed data, like <see cref="ResourceHandle.Id"/>.
/// </summary>
/// <param name="Value">A stable, host-assigned surface index.</param>
public readonly record struct UISurfaceId(int Value);
