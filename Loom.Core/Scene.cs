using System.Numerics;

namespace Loom.Core;

/// <summary>
/// One placed instance of a mesh: a world transform and the world-space <see cref="Bounds"/> that transform
/// produced (cached at add time so the per-frame cull does no matrix work beyond the plane tests).
/// </summary>
/// <param name="Transform">The instance's world matrix — uploaded straight into the per-instance stream.</param>
/// <param name="WorldBounds">The instance's world-space bounds, for frustum culling.</param>
public readonly record struct SceneInstance(Matrix4x4 Transform, Bounds WorldBounds);

/// <summary>
/// A group of instances of one mesh drawn with one technique — the unit a <c>ctx.DrawInstanced</c> consumes.
/// Holds each instance's world transform + cached world bounds, and offers a frustum cull that compacts the
/// visible transforms into a caller-provided span (no per-frame allocation).
/// </summary>
public sealed class InstancedGroup(Mesh mesh, string technique)
{
	private readonly List<SceneInstance> _instances = [];

	/// <summary>The mesh every instance draws.</summary>
	public Mesh Mesh { get; } = mesh;

	/// <summary>The technique (must declare a per-instance <see cref="VertexLayout"/>) the group draws with.</summary>
	public string Technique { get; } = technique;

	/// <summary>The placed instances, in add order.</summary>
	public IReadOnlyList<SceneInstance> Instances => _instances;

	/// <summary>Total number of instances (visible or not).</summary>
	public int Count => _instances.Count;

	/// <summary>Place one instance: <paramref name="localBounds"/> is the mesh's object-space box, transformed to world space and cached.</summary>
	public void Add(Matrix4x4 transform, Bounds localBounds) =>
		_instances.Add(new SceneInstance(transform, localBounds.Transform(transform)));

	/// <summary>
	/// Compact the transforms of the instances whose world bounds intersect <paramref name="frustum"/> into
	/// <paramref name="visible"/>, returning how many were written (the drawn count). <paramref name="visible"/>
	/// must be at least <see cref="Count"/> long. The culled count is <see cref="Count"/> minus the return value.
	/// </summary>
	public int Cull(in Frustum frustum, Span<Matrix4x4> visible)
	{
		if (visible.Length < _instances.Count)
			throw new ArgumentException($"The visible span ({visible.Length}) is smaller than the instance count ({_instances.Count}).", nameof(visible));
		var written = 0;
		foreach (var instance in _instances)
			if (frustum.Intersects(instance.WorldBounds))
				visible[written++] = instance.Transform;
		return written;
	}

	/// <summary>Copy every transform into <paramref name="visible"/> (no culling), returning <see cref="Count"/>.</summary>
	public int CopyAll(Span<Matrix4x4> visible)
	{
		for (var i = 0; i < _instances.Count; i++)
			visible[i] = _instances[i].Transform;
		return _instances.Count;
	}
}

/// <summary>
/// An <em>optional</em>, app-owned draw-list container (PlanningPhase2 §6: the engine offers a <c>Scene</c> and
/// a frustum-cull helper, but never mandates a scene graph or seizes draw submission). A scene is a flat set of
/// <see cref="InstancedGroup"/>s; the app builds it once, culls it each frame against
/// <see cref="Camera.ExtractFrustum"/>, and feeds the surviving transforms to <c>ctx.DrawInstanced</c>. Nothing
/// in the engine requires its use — a demo that draws meshes directly still works.
/// </summary>
public sealed class Scene
{
	private readonly List<InstancedGroup> _groups = [];

	/// <summary>The instanced groups in this scene.</summary>
	public IReadOnlyList<InstancedGroup> Groups => _groups;

	/// <summary>Add and return a new instanced group for <paramref name="mesh"/> drawn with <paramref name="technique"/>.</summary>
	public InstancedGroup AddInstanced(Mesh mesh, string technique)
	{
		var group = new InstancedGroup(mesh, technique);
		_groups.Add(group);
		return group;
	}
}
