using Loom.Core;

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace Loom.DX12;

/// <summary>
/// The DX12 backend's game-UI renderer — the twin of <see cref="DX12ImGuiRenderer"/> for the XAML UI engine
/// (PlanningUI.md §D6), and the DX12 counterpart of <c>Loom.Vulkan/UiRenderer</c>. The "ui" pass's
/// <c>ctx.DrawUI(surface)</c> forwards that surface's composited, neutral <see cref="UiDrawData"/> here,
/// recording into the already-bound render target.
/// <para>
/// MU0 Phase A ships this as a <b>no-op</b>: standing up the seam and proving it runs clean on both backends is
/// the milestone. MU1 fills in the real path — a <c>ui.slang</c> PSO, MSDF atlas upload to a default-heap
/// texture, per-frame grown UPLOAD-heap buffers, and a scissored <c>DrawIndexedInstanced</c> — cloning
/// <see cref="DX12ImGuiRenderer"/> verbatim.
/// </para>
/// </summary>
internal sealed class DX12UiRenderer
{
	/// <summary>
	/// Record one surface's draw data into the open render target. MU0 Phase A: a no-op (the seam is what this
	/// milestone proves). The signature mirrors <see cref="DX12ImGuiRenderer.Render"/> so MU1 only fills the body.
	/// </summary>
	public void Render(ComPtr<ID3D12GraphicsCommandList> cmd, UiDrawData draw, uint fbWidth, uint fbHeight, uint frameSlot)
	{
		// MU0 Phase A no-op. Pixels land in MU1.
	}
}
