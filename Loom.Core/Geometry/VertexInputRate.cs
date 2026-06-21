namespace Loom.Core.Geometry;

/// <summary>How the input assembler advances through a <see cref="VertexLayout"/>'s buffer.</summary>
public enum VertexInputRate
{
	/// <summary>One element per vertex — the mesh's geometry stream (binding 0).</summary>
	PerVertex,

	/// <summary>One element per instance — the per-instance stream a <c>DrawInstanced</c> reads (binding 1).</summary>
	PerInstance,
}
