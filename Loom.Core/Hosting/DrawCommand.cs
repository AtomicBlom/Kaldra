using Loom.Core.Primitives;

namespace Loom.Core.Hosting;

/// <summary>
/// One run of an <see cref="IRenderContext.DrawList{TVertex}"/> submission: a contiguous slice of the index
/// stream, drawn under its own scissor rectangle with one texture bound. This is the backend-neutral draw-list
/// command — the same shape Dear ImGui and the game-UI compositor batch into (a clip rect + a texture + an index
/// range), generalized so any per-frame variable-size indexed stream can be submitted through the public API.
/// </summary>
/// <param name="ClipRect">The scissor rectangle in target pixels; the backend clamps it to the framebuffer.</param>
/// <param name="TextureIndex">Which of the call's bound textures this run samples (index into the <c>textures</c> span).</param>
/// <param name="IndexOffset">First index of the run within the call's index stream.</param>
/// <param name="IndexCount">Number of indices in the run.</param>
/// <param name="VertexOffset">Added to every index before the vertex fetch — lets one 16-bit index stream address more than 64k vertices.</param>
/// <param name="Tag">A per-command value the technique reads from the draw-list push block (the UI uses it for the primitive kind); techniques that don't need it ignore it.</param>
public readonly record struct DrawCommand(
	Rect ClipRect,
	int TextureIndex,
	int IndexOffset,
	int IndexCount,
	int VertexOffset,
	uint Tag);
