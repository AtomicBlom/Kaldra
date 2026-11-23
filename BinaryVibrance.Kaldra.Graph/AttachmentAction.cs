using System.Numerics;
using BinaryVibrance.Kaldra.Core.Graph;

namespace BinaryVibrance.Kaldra.Graph;

/// <summary>
/// The load/store decision for one render target of a raster pass — what the backend feeds into the pass's
/// <c>VkRenderingAttachmentInfo</c> under dynamic rendering. Colour targets carry <see cref="ResourceState.ColorAttachment"/>;
/// depth targets carry <see cref="ResourceState.DepthAttachment"/> or <see cref="ResourceState.DepthReadOnly"/>.
/// </summary>
/// <param name="Resource">The attachment.</param>
/// <param name="State">The attachment's role this pass (colour vs depth-write vs depth-read).</param>
/// <param name="Load">How to treat existing contents on begin.</param>
/// <param name="Store">Whether to keep the contents on end.</param>
/// <param name="ClearValue">The colour to clear a colour target to when <see cref="Load"/> is <see cref="LoadAction.Clear"/>; null for a depth attachment or a non-clearing load.</param>
/// <param name="DepthClear">The depth to clear a depth target to when <see cref="Load"/> is <see cref="LoadAction.Clear"/> — 1 conventionally, 0 for reversed-Z; null for a colour attachment or a non-clearing load. Exactly one of this and <see cref="ClearValue"/> is ever set.</param>
public readonly record struct AttachmentAction(ResourceHandle Resource, ResourceState State, LoadAction Load, StoreAction Store, Vector4? ClearValue = null, float? DepthClear = null);
