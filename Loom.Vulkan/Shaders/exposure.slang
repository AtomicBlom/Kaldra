// Auto-exposure reduction (Planning.md §12 M5 — "Persistent + Compute"). A single-workgroup COMPUTE pass:
// it meters the lit scene's luminance, then adapts an exposure multiplier toward the target and writes it
// into a 1×1 PERSISTENT storage image. Persistent = the value survives across frames, so the adaptation is
// temporal (it eases toward the target rather than snapping) — the cross-frame-history story the milestone
// proves. The tonemap pass then reads this exposure to scale the final image.
//
// The reduction is deliberately simple (a sparse grid of SampleLevel taps in one invocation) — enough to
// prove the compute + persistent path through the graph; a proper parallel multi-stage reduction is a later
// optimisation. Compute has no implicit derivatives, so sampling uses SampleLevel.

[[vk::binding(0, 0)]] Sampler2D litTex;            // the HDR lit scene (sampled)
[[vk::binding(1, 0)]] RWTexture2D<float> exposure; // 1×1 persistent storage image (read-modify-write)

struct PushConstants
{
	float4 p; // x = adaptation rate this frame (≈ rate * dt, saturated)
};

[[vk::push_constant]] PushConstants pc;

[shader("compute")]
[numthreads(1, 1, 1)]
void computeMain(uint3 id : SV_DispatchThreadID)
{
	const int n = 16;
	float sum = 0.0;
	for (int y = 0; y < n; y++)
		for (int x = 0; x < n; x++)
		{
			float2 uv = (float2(x, y) + 0.5) / n;
			float3 c = litTex.SampleLevel(uv, 0.0).rgb;
			sum += dot(c, float3(0.2126, 0.7152, 0.0722)); // Rec.709 luma
		}
	float avg = max(sum / (n * n), 1e-4);

	// Aim the average toward a mid-grey key; clamp tight so a mostly-dark frame doesn't blow the cube out.
	float target = clamp(0.5 / avg, 0.6, 1.6);

	// First frame the persistent image is uninitialised — clamp whatever garbage is there into the plausible
	// exposure range so the adaptation eases from a sane value rather than a wild one.
	float prev = clamp(exposure[int2(0, 0)], 0.3, 1.8);

	exposure[int2(0, 0)] = lerp(prev, target, saturate(pc.p.x)); // ease toward the target (temporal adaptation)
}
