using Silk.NET.Direct3D12;

using GraphState = Loom.Graph.ResourceState;

namespace Loom.DX12;

/// <summary>
/// Concretises the Execute layer's backend-neutral <see cref="GraphState"/> into a
/// <c>D3D12_RESOURCE_STATES</c> — the DX12 counterpart of the Vulkan backend's <c>StateMapping</c>. Where
/// Vulkan needs a (layout, stage, access) triple per state, D3D12 folds all three into one resource-state
/// bitmask, so the mapping is simpler.
/// <para>
/// <b>Key difference from Vulkan.</b> A Vulkan barrier accepts <c>Undefined</c> as its <em>old</em> layout
/// ("don't care about prior contents"); a D3D12 transition barrier requires the resource's <em>exact</em>
/// current state. So the executor never maps a transition's neutral <c>From</c> — it tracks each resource's
/// actual D3D12 state and emits transitions from the tracked state to <see cref="For"/> the required
/// <c>To</c>. <see cref="GraphState.Undefined"/> is therefore only ever an <em>initial</em> tracked state
/// (mapped to <c>COMMON</c>), never a transition target.
/// </para>
/// </summary>
internal static class DX12StateMapping
{
	public static ResourceStates For(GraphState state) => state switch
	{
		GraphState.Undefined => ResourceStates.Common,
		GraphState.ColorAttachment => ResourceStates.RenderTarget,
		GraphState.DepthAttachment => ResourceStates.DepthWrite,
		GraphState.DepthReadOnly => ResourceStates.DepthRead,
		// Sampled in a shader — readable from either the pixel stage (fullscreen/lighting) or a non-pixel stage
		// (a compute reduction), matching the Vulkan mapping's fragment|compute scope.
		GraphState.ShaderRead => ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource,
		GraphState.Storage => ResourceStates.UnorderedAccess,
		GraphState.VertexInput => ResourceStates.VertexAndConstantBuffer,
		GraphState.Present => ResourceStates.Present,
		_ => throw new ArgumentOutOfRangeException(nameof(state), state, "No DX12 resource-state mapping."),
	};
}
