using System.Globalization;
using System.Numerics;
using System.Text;
using Loom.Core.Geometry;
using Loom.Core.Graph;

namespace Loom.Core.Hosting;

/// <summary>
/// One frame's flattened draw list — a variable-size vertex stream, its 16-bit index stream, and the batched
/// <see cref="DrawCommand"/>s that scissor + texture + index-range it. Backend-neutral (no graphics-API types),
/// so the whole producer pipeline up to GPU upload is CPU-testable against <see cref="Dump"/> — the draw-list
/// analog of the render graph's <c>BarrierPlan</c>/<c>AliasPlan</c> snapshots. A producer (the UI compositor)
/// hands a set of these in via <see cref="FrameInputs.DrawLists"/>; a pass submits one through
/// <see cref="IRenderContext.DrawList{TVertex}"/>. Rebuilt only when its source changes; an unchanged frame
/// re-submits the same <see cref="Revision"/>.
/// </summary>
public sealed class DrawListData
{
	/// <summary>An empty list (no geometry) — the no-op a clean or disabled producer hands the channel.</summary>
	public static DrawListData Empty { get; } = new();

	/// <summary>The vertex stream (target-pixel positions; premultiplied RGBA16F colour).</summary>
	public ReadOnlyMemory<OverlayVertex> Vertices { get; init; }

	/// <summary>The index stream (16-bit + <see cref="DrawCommand.VertexOffset"/> for &gt;64k vertices).</summary>
	public ReadOnlyMemory<ushort> Indices { get; init; }

	/// <summary>The batched draw commands, in submission order.</summary>
	public ReadOnlyMemory<DrawCommand> Commands { get; init; }

	/// <summary>The pixel size of the coordinate space the geometry is authored in, for the consumer's pixel→clip scale.</summary>
	public Vector2 CoordinateSize { get; init; }

	/// <summary>True when this list targets an HDR surface (lets the consuming technique lift colour to display nits).</summary>
	public bool IsHdrTarget { get; init; }

	/// <summary>A monotonically increasing damage/no-op token: an unchanged list re-submits the same revision.</summary>
	public uint Revision { get; init; }

	/// <summary>
	/// A deterministic, human-readable text snapshot of the whole draw list — the "golden draw-list" a producer's
	/// CPU tests assert against, mirroring <see cref="RenderGraph.Dump"/>. Stable across platforms (invariant-culture
	/// formatting), so it diffs cleanly in CI.
	/// </summary>
	public string Dump()
	{
		var c = CultureInfo.InvariantCulture;
		var vertices = Vertices.Span;
		var indices = Indices.Span;
		var commands = Commands.Span;

		var sb = new StringBuilder();
		sb.Append(c, $"DrawListData (rev {Revision}, coords {CoordinateSize.X:0.###}x{CoordinateSize.Y:0.###}, hdr={IsHdrTarget})\n");

		sb.Append(c, $"  Vertices ({vertices.Length}):\n");
		for (var i = 0; i < vertices.Length; i++)
		{
			ref readonly var v = ref vertices[i];
			sb.Append(c, $"    [{i}] pos=({v.Position.X:0.###},{v.Position.Y:0.###}) uv=({v.Uv.X:0.###},{v.Uv.Y:0.###})");
			sb.Append(c, $" col=({(float)v.Color.R:0.###},{(float)v.Color.G:0.###},{(float)v.Color.B:0.###},{(float)v.Color.A:0.###})");
			sb.Append(c, $" custom=({v.Custom.X:0.###},{v.Custom.Y:0.###},{v.Custom.Z:0.###},{v.Custom.W:0.###})\n");
		}

		sb.Append(c, $"  Indices ({indices.Length}):");
		for (var i = 0; i < indices.Length; i++)
			sb.Append(c, $" {indices[i]}");
		sb.Append('\n');

		sb.Append(c, $"  Commands ({commands.Length}):\n");
		for (var i = 0; i < commands.Length; i++)
		{
			ref readonly var cmd = ref commands[i];
			var r = cmd.ClipRect;
			// Format the clip rect's fields inline (invariant culture) rather than via Rect.ToString() so the whole
			// dump is deterministic across cultures — the golden-draw-list snapshot must diff cleanly in CI.
			sb.Append(c, $"    [{i}] tag={cmd.Tag} tex={cmd.TextureIndex} clip=({r.X:0.###},{r.Y:0.###} {r.Width:0.###}x{r.Height:0.###})");
			sb.Append(c, $" idx={cmd.IndexOffset}+{cmd.IndexCount} vtx={cmd.VertexOffset}\n");
		}

		return sb.ToString();
	}
}
