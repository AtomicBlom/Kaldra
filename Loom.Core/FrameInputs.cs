namespace Loom.Core;

/// <summary>
/// The per-frame data a host hands the engine for one <see cref="IEngine.Render"/> call (Planning.md §5's
/// <c>graph.Render(frameInputs)</c>). This is the single channel for everything that varies frame to frame —
/// the camera, timing, the hovered object, the UI draw data — so pass closures stay pure: they <em>pull</em>
/// these from <see cref="IRenderContext"/> rather than capturing host state (Planning.md §8). Immutable: the
/// host builds a fresh one each frame.
/// <para>
/// Matrices are stated in a backend-neutral convention (right-handed, +Y up; <c>System.Numerics</c>); the
/// backend applies its own clip-space fix-up (the Vulkan Y-flip, identity on D3D12) on ingest, so the host
/// never bakes an API-specific NDC convention (Planning.md §8 portability; M7-B5).
/// </para>
/// </summary>
public sealed record FrameInputs
{
	/// <summary>Seconds elapsed since the previous frame; surfaced as <see cref="IRenderContext.DeltaTime"/>.</summary>
	public double DeltaSeconds { get; init; }

	/// <summary>Elapsed seconds since startup, for time-driven effects; surfaced as <see cref="IRenderContext.Time"/>.</summary>
	public float Time { get; init; }

	/// <summary>The camera to render from this frame; surfaced as <see cref="IRenderContext.Camera"/>. Neutral convention — the backend applies its clip-space fix-up.</summary>
	public Camera Camera { get; init; } = Camera.Identity;

	/// <summary>The sun's view + projection this frame; surfaced as <see cref="IRenderContext.SunView"/> for the shadow pass and the lighting shadow test.</summary>
	public Camera SunView { get; init; } = Camera.Identity;

	/// <summary>The object id under the cursor (0 = nothing); surfaced as <see cref="IRenderContext.HoverId"/> to seed the JFA outline.</summary>
	public uint HoverId { get; init; }

	/// <summary>
	/// This frame's Dear ImGui draw data (the native <c>ImDrawData*</c> from <c>ImGui.Render()</c>), forwarded by
	/// the HUD pass's <c>ctx.DrawImGui()</c>. <see cref="nint.Zero"/> (the default) draws no UI. Meaningful only
	/// after <see cref="IEngine.EnableImGui"/>. The pointer is the UI-agnostic hand-off (Planning.md §8).
	/// </summary>
	public nint ImGuiDrawData { get; init; }

	/// <summary>
	/// This frame's composited game-UI surfaces — each <see cref="UISurfaceId"/> paired with the neutral
	/// <see cref="UIDrawData"/> the host's compositor produced for it (PlanningUI.md §Core surface additions). A
	/// pass names one with <c>ctx.DrawUI(id)</c>; the backend looks it up here and records it. Empty (the default)
	/// draws no UI. Meaningful only after <see cref="IEngine.EnableUI"/>. Pulled in the pass closure, never
	/// captured, so the graph stays frame-independent (Planning.md §8).
	/// </summary>
	public IReadOnlyList<(UISurfaceId Id, UIDrawData Draw)> UISurfaces { get; init; } = [];
}
