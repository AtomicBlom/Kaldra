using System.Globalization;
using System.Numerics;
using System.Text;

namespace Loom.Core;

/// <summary>
/// The neutral, flattened draw stream for one UI surface — the output of the default <c>DrawListCompositor</c>
/// (PlanningUI.md §D2.5) and the contract handed to the backend's UI renderer via
/// <see cref="IRenderContext.DrawUI"/>. Backend-neutral (no graphics-API types), so the whole pipeline up to GPU
/// upload is asserted by CPU tests against <see cref="Dump"/> — the UI analog of the render graph's
/// <c>BarrierPlan</c>/<c>AliasPlan</c> snapshots. Rebuilt only on a dirty frame; a clean frame re-submits the
/// last <see cref="Revision"/>.
/// </summary>
public sealed class UIDrawData
{
	/// <summary>An empty surface (no geometry) — the no-op a disabled or fully-clean surface hands the seam.</summary>
	public static UIDrawData Empty { get; } = new();

	/// <summary>The vertex stream (surface-pixel positions; premultiplied RGBA16F colour).</summary>
	public ReadOnlyMemory<UiVertex> Vertices { get; init; }

	/// <summary>The index stream (16-bit + <see cref="UiDrawCommand.VertexOffset"/> for &gt;64k vertices).</summary>
	public ReadOnlyMemory<ushort> Indices { get; init; }

	/// <summary>The batched draw commands, in submission order.</summary>
	public ReadOnlyMemory<UiDrawCommand> Commands { get; init; }

	/// <summary>The surface's pixel size, for the backend's pixel→clip push.</summary>
	public Vector2 SurfaceSize { get; init; }

	/// <summary>True when this surface renders to an HDR target (drives colour linearization in <c>ui.slang</c>).</summary>
	public bool IsHdrSurface { get; init; }

	/// <summary>A monotonically increasing damage/no-op token: an unchanged surface re-submits the same revision.</summary>
	public uint Revision { get; init; }

	/// <summary>
	/// A deterministic, human-readable text snapshot of the whole draw stream — the "golden draw-list" the UI's
	/// CPU tests assert against (PlanningUI.md §D8.1), mirroring <see cref="RenderGraph.Dump"/>. Stable across
	/// platforms (invariant-culture formatting), so it diffs cleanly in CI.
	/// </summary>
	public string Dump()
	{
		var c = CultureInfo.InvariantCulture;
		var vertices = Vertices.Span;
		var indices = Indices.Span;
		var commands = Commands.Span;

		var sb = new StringBuilder();
		sb.Append(c, $"UiDrawData (rev {Revision}, surface {SurfaceSize.X:0.###}x{SurfaceSize.Y:0.###}, hdr={IsHdrSurface})\n");

		sb.Append(c, $"  Vertices ({vertices.Length}):\n");
		for (var i = 0; i < vertices.Length; i++)
		{
			ref readonly var v = ref vertices[i];
			sb.Append(c, $"    [{i}] pos=({v.Position.X:0.###},{v.Position.Y:0.###}) uv=({v.Uv.X:0.###},{v.Uv.Y:0.###})");
			sb.Append(c, $" col=({(float)v.Color.R:0.###},{(float)v.Color.G:0.###},{(float)v.Color.B:0.###},{(float)v.Color.A:0.###})");
			sb.Append(c, $" params=({v.Params.X:0.###},{v.Params.Y:0.###},{v.Params.Z:0.###},{v.Params.W:0.###})\n");
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
			sb.Append(c, $"    [{i}] {cmd.Kind} tex={cmd.TextureId} clip=({r.X:0.###},{r.Y:0.###} {r.Width:0.###}x{r.Height:0.###})");
			sb.Append(c, $" idx={cmd.IndexOffset}+{cmd.IndexCount} vtx={cmd.VertexOffset}\n");
		}

		return sb.ToString();
	}
}
