namespace BinaryVibrance.Kaldra.Core.Geometry;

/// <summary>
/// Per-instance data uploaded once, to GPU-resident memory, and drawn from by range — the instance counterpart of a
/// <see cref="Mesh"/>, for instances that don't change from frame to frame (a placed forest, a row of lamps, the chunks
/// of a trail that have filled). Minted by <c>IEngine.CreateInstances</c>, drawn with <c>DrawCall.Static</c>, freed by
/// <c>IEngine.ReleaseInstances</c>. A CPU instance span (<c>DrawCall.From</c>) is uploaded again every frame it is
/// drawn; one of these costs nothing per frame however large it is.
/// </summary>
/// <param name="Storage">The buffer's identity: the engine stores it as a mesh whose vertices are the instances, so it is
/// created, resolved and released exactly as a mesh is.</param>
/// <param name="Count">How many instances it holds.</param>
/// <param name="Stride">Bytes per instance: the instance layout's stride.</param>
public readonly record struct InstanceBuffer(Mesh Storage, int Count, int Stride)
{
	/// <summary>
	/// Throw unless a technique advancing its per-instance stream <paramref name="techniqueStride"/> bytes per instance
	/// reads this buffer as it was laid out; a technique with no instance stream (0) would ignore it.
	/// </summary>
	public void CheckInstanceStride(int techniqueStride, string technique)
	{
		if (techniqueStride == Stride)
			return;
		throw new InvalidOperationException(techniqueStride == 0
			? $"Technique '{technique}' has no per-instance stream, so it would ignore the instance buffer drawn with it. " +
			  "Give it an Instance layout, or draw without instances."
			: $"Technique '{technique}' advances its per-instance stream {techniqueStride} bytes per instance, but the " +
			  $"instance buffer was created with {Stride}-byte instances. Create it with the VertexLayout the technique " +
			  "names as its Instance stream, so the two cannot drift.");
	}
}
