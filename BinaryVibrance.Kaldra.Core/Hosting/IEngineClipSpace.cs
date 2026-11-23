using System.Numerics;

namespace BinaryVibrance.Kaldra.Core.Hosting;

/// <summary>
/// Exposes the engine's clip-space convention as a neutral matrix, so an app-side view (a
/// <c>BinaryVibrance.Kaldra.Concepts.Camera</c>) can compose it into a view-projection without naming a backend's NDC convention.
/// Shaders are single-source bytecode, so the Vulkan/D3D12 NDC difference lives in this matrix rather than in the
/// shader: an app computes <c>view * projection * ClipSpaceCorrection</c> and the result lands in the engine's
/// clip space on every backend. A narrow capability interface (rather than passing the whole <see cref="IEngine"/>)
/// so a camera depends only on this — and can take <c>null</c> for it, meaning "no correction" (a test, or a
/// +Y-up-native target that needs none).
/// </summary>
public interface IEngineClipSpace
{
	/// <summary>
	/// The clip-space fix-up to post-multiply onto a right-handed, +Y-up view-projection: <c>diag(1,-1,1,1)</c> on a
	/// backend that puts NDC Y=-1 at the top of its targets (Vulkan natively, and DirectX12, which pairs it with a
	/// negative-height viewport), identity on one that puts +1 there (WebGPU). Composing it lets a host-built projection
	/// render upright on every backend without the host hardcoding a backend's clip-space Y direction. Anything that
	/// maps between screen pixels and NDC — <c>Matrix4x4.TryProjectToScreen</c> and <c>TryUnprojectFromScreen</c> in
	/// <c>BinaryVibrance.Kaldra.Core.Geometry</c> — takes the same matrix, to know which way NDC Y runs.
	/// </summary>
	Matrix4x4 ClipSpaceCorrection { get; }
}
