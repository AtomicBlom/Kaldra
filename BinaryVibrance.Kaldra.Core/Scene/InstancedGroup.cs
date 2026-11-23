using System.Numerics;
using BinaryVibrance.Kaldra.Core.Geometry;

namespace BinaryVibrance.Kaldra.Core.Scene;

/// <summary>
/// A group of instances of one mesh drawn with one technique — the unit a <c>ctx.Draw</c> with a <c>DrawCall.Instances</c> source consumes.
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
