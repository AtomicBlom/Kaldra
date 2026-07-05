using Loom.Core.Graph;
using Loom.Graph;

namespace Loom.Backend.WebGPU;

/// <summary>
/// The WebGPU counterpart of the other backends' state mappings — but WebGPU tracks resource state itself, so
/// there are no barriers or layouts to translate. What remains of <see cref="ResourceState"/>'s job is the
/// <b>usage-flag union</b>: WebGPU requires every usage a texture or buffer will <em>ever</em> have declared at
/// creation, so each resource's states across the whole plan are folded into one usage mask up front.
/// </summary>
internal static class WebGpuStateMapping
{
	// GPUTextureUsage flags (WebGPU spec values; mirrored by the JS shim, which passes them through verbatim).
	public const int TextureCopySrc = 0x01;
	public const int TextureCopyDst = 0x02;
	public const int TextureBinding = 0x04;
	public const int StorageBinding = 0x08;
	public const int RenderAttachment = 0x10;

	// GPUBufferUsage flags.
	public const int BufferMapRead = 0x0001;
	public const int BufferCopySrc = 0x0004;
	public const int BufferCopyDst = 0x0008;
	public const int BufferIndex = 0x0010;
	public const int BufferVertex = 0x0020;
	public const int BufferUniform = 0x0040;
	public const int BufferStorage = 0x0080;

	/// <summary>The texture-usage bits a single <see cref="ResourceState"/> implies.</summary>
	public static int TextureUsageOf(ResourceState state) => state switch
	{
		ResourceState.ColorAttachment => RenderAttachment,
		ResourceState.DepthAttachment => RenderAttachment,
		ResourceState.DepthReadOnly => RenderAttachment,
		ResourceState.ShaderRead => TextureBinding,
		ResourceState.Storage => StorageBinding,
		ResourceState.Present => RenderAttachment,
		_ => 0,
	};

	/// <summary>
	/// Union the usage bits for every state <paramref name="resourceId"/> takes anywhere in the plan, plus
	/// <see cref="TextureCopySrc"/> when it is a readback/capture source. Computed once per graph at
	/// resource-build time.
	/// </summary>
	public static int UnionTextureUsage(BarrierPlan plan, int resourceId, bool copySource)
	{
		var usage = copySource ? TextureCopySrc : 0;
		foreach (var pass in plan.Passes)
		{
			foreach (var transition in pass.Transitions)
				if (transition.Resource.Id == resourceId)
					usage |= TextureUsageOf(transition.To);
			foreach (var attachment in pass.Attachments)
				if (attachment.Resource.Id == resourceId)
					usage |= TextureUsageOf(attachment.State);
		}
		return usage;
	}
}
