using Loom.Core;
using Silk.NET.Vulkan;

namespace Loom.Vulkan;

/// <summary>
/// The backend's game-UI renderer — the twin of <see cref="ImGuiRenderer"/> for the XAML UI engine
/// (PlanningUI.md §D6). The "ui" pass's <c>ctx.DrawUI(surface)</c> forwards that surface's composited, neutral
/// <see cref="UIDrawData"/> here, recording into the already-open rendering scope.
/// <para>
/// MU0 Phase A ships this as a <b>no-op</b>: standing up the seam (the Core neutral types +
/// <c>EnableUI</c>/<c>DrawUI</c>/<c>UiSurfaces</c>) and proving it runs clean on both backends is the milestone.
/// MU1 fills in the real path — a <c>ui.slang</c> pipeline, MSDF atlas upload, per-frame grown vertex/index
/// buffers, and a scissored <c>vkCmdDrawIndexed</c> — cloning <see cref="ImGuiRenderer"/> verbatim.
/// </para>
/// </summary>
internal sealed class UIRenderer
{
	/// <summary>
	/// Record one surface's draw data into the open rendering scope. MU0 Phase A: a no-op (the seam is what this
	/// milestone proves). The signature mirrors <see cref="ImGuiRenderer.Render"/> so MU1 only fills the body.
	/// </summary>
	public void Render(CommandBuffer cmd, UIDrawData draw, Extent2D fbExtent, int frameSlot)
	{
		// MU0 Phase A no-op. Pixels land in MU1.
	}
}
