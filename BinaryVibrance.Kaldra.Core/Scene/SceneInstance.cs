using System.Numerics;
using BinaryVibrance.Kaldra.Core.Geometry;

namespace BinaryVibrance.Kaldra.Core.Scene;

/// <summary>
/// One placed instance of a mesh: a world transform and the world-space <see cref="Bounds"/> that transform
/// produced (cached at add time so the per-frame cull does no matrix work beyond the plane tests).
/// </summary>
/// <param name="Transform">The instance's world matrix — uploaded straight into the per-instance stream.</param>
/// <param name="WorldBounds">The instance's world-space bounds, for frustum culling.</param>
public readonly record struct SceneInstance(Matrix4x4 Transform, Bounds WorldBounds);
