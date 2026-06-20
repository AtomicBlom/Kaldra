namespace Loom.Core.Hosting;

/// <summary>
/// Identifies one draw list a frame may carry in <see cref="FrameInputs.DrawLists"/>. A producer (e.g. the UI
/// host's compositor) assigns a stable id per list — the screen-space overlay and each in-world surface get a
/// distinct one — and a pass names it to submit that list (<see cref="IRenderContext.DrawLists"/>). Backend-neutral
/// managed data, like <see cref="Loom.Core.Graph.ResourceHandle.Id"/>.
/// </summary>
/// <param name="Value">A stable, producer-assigned list index.</param>
public readonly record struct DrawListId(int Value);
