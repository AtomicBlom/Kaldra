using Silk.NET.Vulkan;

using GraphState = Loom.Graph.ResourceState;

namespace Loom.Vulkan;

/// <summary>
/// Concretises the Execute layer's backend-neutral <see cref="GraphState"/> into the Vulkan triple a
/// <c>vkCmdPipelineBarrier2</c> image memory barrier needs: the image <b>layout</b>, the
/// <b>pipeline-stage</b> scope, and the <b>access</b> scope (synchronization2). A transition's
/// <see cref="Loom.Graph.ResourceTransition.From"/> supplies the barrier's old layout + source
/// stage/access; its <see cref="Loom.Graph.ResourceTransition.To"/> supplies the new layout + destination
/// scope. This table is exactly the layout/stage/access knowledge kept out of Loom.Graph
/// so a second backend can map the same states its own way.
/// </summary>
internal static class VulkanStateMapping
{
	/// <summary>The Vulkan layout/stage/access a resource state corresponds to.</summary>
	public readonly record struct Scope(ImageLayout Layout, PipelineStageFlags2 Stage, AccessFlags2 Access);

	/// <summary>Map a logical resource state to its Vulkan layout + synchronisation scope.</summary>
	public static Scope For(GraphState state) => state switch
	{
		// No prior contents; nothing to wait on. The classic acquire/first-use source scope.
		GraphState.Undefined => new(ImageLayout.Undefined, PipelineStageFlags2.TopOfPipeBit, AccessFlags2.None),

		// Colour render target. Read included so a Load/blend (read-modify-write) is covered.
		GraphState.ColorAttachment => new(
			ImageLayout.ColorAttachmentOptimal,
			PipelineStageFlags2.ColorAttachmentOutputBit,
			AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ColorAttachmentReadBit),

		// Depth render target being written (depth test + write).
		GraphState.DepthAttachment => new(
			ImageLayout.DepthAttachmentOptimal,
			PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
			AccessFlags2.DepthStencilAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentReadBit),

		// Depth bound read-only as an attachment (test against existing depth, no write).
		GraphState.DepthReadOnly => new(
			ImageLayout.DepthReadOnlyOptimal,
			PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
			AccessFlags2.DepthStencilAttachmentReadBit),

		// Sampled in a shader (the .Samples consumer). Covers both fragment (fullscreen/lighting) and compute
		// (the auto-exposure reduction samples lit) so the transition makes the read visible to either stage.
		GraphState.ShaderRead => new(
			ImageLayout.ShaderReadOnlyOptimal,
			PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
			AccessFlags2.ShaderSampledReadBit),

		// Compute storage image (read-write). Unused until the first compute pass, but defined for completeness.
		GraphState.Storage => new(
			ImageLayout.General,
			PipelineStageFlags2.ComputeShaderBit,
			AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit),

		// A buffer read as vertex-attribute input (GPU particles). Buffers have no layout — the layout field
		// is ignored for the buffer memory barrier the executor emits; only the stage/access scope matters.
		GraphState.VertexInput => new(
			ImageLayout.Undefined,
			PipelineStageFlags2.VertexAttributeInputBit,
			AccessFlags2.VertexAttributeReadBit),

		// Terminal: hand the image to the presentation engine.
		GraphState.Present => new(ImageLayout.PresentSrcKhr, PipelineStageFlags2.BottomOfPipeBit, AccessFlags2.None),

		_ => throw new VulkanException($"No Vulkan scope mapping for resource state {state}."),
	};
}
