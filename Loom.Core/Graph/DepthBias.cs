namespace Loom.Core.Graph;

/// <summary>
/// A constant + slope-scaled offset the rasterizer adds to every fragment's depth, baked into the technique's
/// pipeline. The shadow-mapping tool: rendering a shadow map with a small bias pushes the caster's own depth away
/// from the light so the receiving surface doesn't shadow itself (acne) — at the cost of contact shadows detaching
/// from the caster (peter-panning) if the bias is too large.
/// </summary>
/// <param name="Constant">
/// A flat offset in depth-buffer units. <b>Not portable between backends:</b> Vulkan multiplies it by an
/// implementation-defined value <c>r</c> (the smallest resolvable difference for the depth format), while D3D12
/// applies <c>Constant × 2^(exponent − 23)</c> for a float depth buffer. The same number therefore biases by
/// different amounts on Vulkan and DirectX12 — tune per backend, or lean on <paramref name="SlopeScale"/>, which is
/// well defined on both.
/// </param>
/// <param name="SlopeScale">
/// Scales the offset by the fragment's depth slope, so steeply-angled surfaces — the ones whose depth changes most
/// across a texel, and which therefore self-shadow first — get proportionally more bias and flat surfaces get almost
/// none. The portable knob: both backends multiply by the same max depth-slope term.
/// </param>
/// <param name="Clamp">
/// Upper bound on the total offset (0 = unclamped), so a near-edge-on surface's slope term can't run away and
/// detach its shadow entirely.
/// </param>
public readonly record struct DepthBias(int Constant, float SlopeScale = 0f, float Clamp = 0f);
