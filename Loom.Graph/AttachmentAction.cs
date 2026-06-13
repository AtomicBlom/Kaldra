using Loom.Core;

namespace Loom.Graph;

/// <summary>
/// The load/store decision for one render target of a raster pass — what the backend feeds into the pass's
/// <c>VkRenderingAttachmentInfo</c> under dynamic rendering. Colour targets carry <see cref="ResourceState.ColorAttachment"/>;
/// depth targets carry <see cref="ResourceState.DepthAttachment"/> or <see cref="ResourceState.DepthReadOnly"/>.
/// </summary>
/// <param name="Resource">The attachment.</param>
/// <param name="State">The attachment's role this pass (colour vs depth-write vs depth-read).</param>
/// <param name="Load">How to treat existing contents on begin.</param>
/// <param name="Store">Whether to keep the contents on end.</param>
public readonly record struct AttachmentAction(ResourceHandle Resource, ResourceState State, LoadAction Load, StoreAction Store);
