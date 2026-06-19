using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Loom.Core;

/// <summary>
/// Identifies one UI surface a frame may carry — the screen-space overlay and each in-world quad get a distinct id
/// (PlanningUI.md §Core surface additions). The host composites each surface to a <see cref="UiDrawData"/> and
/// hands the engine the set via <see cref="FrameInputs.UiSurfaces"/>; a pass names one with
/// <see cref="IRenderContext.DrawUI"/>. Backend-neutral managed data, like <see cref="ResourceHandle.Id"/>.
/// </summary>
/// <param name="Value">A stable, host-assigned surface index.</param>
public readonly record struct UiSurfaceId(int Value);

/// <summary>Well-known <see cref="UiSurfaceId"/>s. The overlay HUD is surface 0; in-world quads take higher ids.</summary>
public static class UiSurface
{
	/// <summary>The screen-space overlay surface (the HUD), composited at native resolution over the backbuffer.</summary>
	public static readonly UiSurfaceId Hud = new(0);
}

/// <summary>
/// One UI vertex in the neutral draw stream produced by the default compositor (PlanningUI.md §D2.5). Positions
/// are in surface pixels (already post-transform, post-DPI), so the backend's UI renderer only applies the
/// pixel→clip scale+translate (the ImGui vertex push). Backend-neutral (<c>System.Numerics</c> + <see cref="Half4"/>),
/// <see cref="StructLayout"/> sequential so the layout matches what the backend uploads.
/// </summary>
/// <param name="Position">Surface-pixel position (post-transform, post-DPI).</param>
/// <param name="Uv">Atlas UV (glyph/image) or local rect-space coordinate (for the analytic SDF rounding).</param>
/// <param name="Color">Premultiplied RGBA16F colour (carries above-paper-white HDR + wide gamut).</param>
/// <param name="Params">Per-primitive scratch: rounded-rect radii / border half-width / SDF range (kind-dependent).</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct UiVertex(Vector2 Position, Vector2 Uv, Half4 Color, Vector4 Params);

/// <summary>
/// The primitive family a <see cref="UiDrawCommand"/> draws — selects the branch the single <c>ui.slang</c>
/// fragment switches on (PlanningUI.md §D2.7). A flat <see cref="byte"/> so it rides the command cheaply.
/// </summary>
public enum UiPrimKind : byte
{
	/// <summary>A solid axis-aligned fill (white-texel sampled).</summary>
	Fill,

	/// <summary>An analytic rounded rectangle (radii in <see cref="UiVertex.Params"/>).</summary>
	RoundedRect,

	/// <summary>An analytic (optionally rounded) border stroke (half-width in <see cref="UiVertex.Params"/>).</summary>
	Border,

	/// <summary>An MSDF glyph sampled from a font atlas page.</summary>
	Glyph,

	/// <summary>A tinted image sampled from a UI image atlas or an in-world source target.</summary>
	Image,
}

/// <summary>
/// A single batched draw within a <see cref="UiDrawData"/>: a contiguous run of indices sharing a primitive
/// family, texture, and scissor. The default compositor emits a new command only when one of those changes
/// (PlanningUI.md §D2.6 — atlas-everything + state-run batching), so thousands of rects/glyphs collapse into one
/// indexed draw. Mirrors an ImGui draw command (texture + clip + index range).
/// </summary>
/// <param name="Kind">The primitive family this run draws.</param>
/// <param name="TextureId">The atlas/source texture (<c>0</c> = the 1×1 white texel used by solid fills).</param>
/// <param name="ClipRect">The scissor rectangle, in surface pixels.</param>
/// <param name="IndexOffset">First index of the run (into <see cref="UiDrawData.Indices"/>).</param>
/// <param name="IndexCount">Number of indices in the run.</param>
/// <param name="VertexOffset">Value added to each index before fetching a vertex (the ImGui <c>VtxOffset</c> trick for &gt;64k vertices).</param>
public readonly record struct UiDrawCommand(
	UiPrimKind Kind, int TextureId, Rect ClipRect,
	int IndexOffset, int IndexCount, int VertexOffset);

/// <summary>
/// The neutral, flattened draw stream for one UI surface — the output of the default <c>DrawListCompositor</c>
/// (PlanningUI.md §D2.5) and the contract handed to the backend's UI renderer via
/// <see cref="IRenderContext.DrawUI"/>. Backend-neutral (no graphics-API types), so the whole pipeline up to GPU
/// upload is asserted by CPU tests against <see cref="Dump"/> — the UI analog of the render graph's
/// <c>BarrierPlan</c>/<c>AliasPlan</c> snapshots. Rebuilt only on a dirty frame; a clean frame re-submits the
/// last <see cref="Revision"/>.
/// </summary>
public sealed class UiDrawData
{
	/// <summary>An empty surface (no geometry) — the no-op a disabled or fully-clean surface hands the seam.</summary>
	public static UiDrawData Empty { get; } = new();

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
