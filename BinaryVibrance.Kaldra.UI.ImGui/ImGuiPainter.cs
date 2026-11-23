using System.Numerics;
using BinaryVibrance.Kaldra.Core.Geometry;
using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;
using BinaryVibrance.Kaldra.Core.Primitives;
using Im = ImGuiNET.ImGui;

namespace BinaryVibrance.Kaldra.UI.ImGui;

/// <summary>
/// Renders the host's Dear ImGui draw data through the public engine draw-list API — the bridge between the global
/// ImGui context and the render graph, with no backend knowledge. It is the twin of <c>BinaryVibrance.Kaldra.UI.Vysara</c>'s painter:
/// because an ImGui draw list is the same shape as any other (vertices + 16-bit indices + clip/texture/range
/// commands), the backends need no ImGui renderer at all. Create it once after the engine and the ImGui context
/// exist (it registers the <c>imgui</c> technique and uploads the font atlas through the public API), keep it for the
/// engine's lifetime, and call <see cref="Draw"/> from a pass that blends the target. A painter is a stable handle
/// bundle, so a pass closure may capture it.
/// </summary>
public sealed class ImGuiPainter
{
	private const string Technique = "imgui";
	private const uint White = 0xFFFFFFFFu;
	// IM_COL32 yellow — the frustum-freeze wireframe colour.
	private const uint LineColor = 0xFF00FFFFu;
	private const float LineThickness = 2f;

	// Resource-viewer thumbnail grid, in framebuffer pixels — laid out to the right of the ImGui name list.
	private const float ThumbX = 300f, ThumbY = 30f, ThumbW = 200f, ThumbH = 112f, ThumbGap = 10f;

	// ImGui's ImDrawVert vertex layout: pixel position, uv, packed RGBA8 colour (20 bytes). imgui.slang reads these
	// at locations 0/1/2 (POSITION / TEXCOORD0 / COLOR0); the UNorm4 colour unpacks to a float4 in [0,1].
	private static readonly VertexLayout VertexLayout = new(
		new VertexAttribute("POSITION", VertexFormat.Float2),
		new VertexAttribute("TEXCOORD", VertexFormat.Float2),
		new VertexAttribute("COLOR", VertexFormat.UNorm4));

	private readonly Texture _font;
	// The atlas uv of a solid white texel — line quads sample it so they render as a flat colour.
	private readonly Vector2 _whiteUv;

	// Per-frame scratch, grown on demand so a steady-state frame allocates nothing (no GC in the render loop).
	// The backend copies each DrawList submission into its own ring synchronously, so the painter may
	// refill these between submissions in one frame (UI, then thumbnails, then lines).
	//
	// That refilling is exactly why every submission here is DrawListIdentity.Transient: these spans do not survive
	// the call, and ImGui rebuilds its geometry from scratch every frame anyway, so there is no revision to hold a
	// kept buffer against. Naming a stable stream would promise the backend bytes that are already gone.
	private ImGuiVertex[] _verts = [];
	private ushort[] _indices = [];
	private DrawCommand[] _commands = [];
	private TextureRef[] _thumbTextures = [];

	// The host sets these each frame (empty by default): which graph resources to preview, and the frustum-freeze
	// line segments (x0,y0,x1,y1 in framebuffer pixels) to overlay.
	private int[] _thumbnails = [];
	private int _thumbnailCount;
	private Vector4[] _lines = [];
	private int _lineCount;

	private ImGuiPainter(Texture font, Vector2 whiteUv)
	{
		_font = font;
		_whiteUv = whiteUv;
	}

	/// <summary>
	/// Register the <c>imgui</c> technique and upload the current ImGui context's font atlas through the public API,
	/// returning a painter to keep. Call once after the engine exists and the host has created + configured the ImGui
	/// context (fonts added, style set); the painter builds the atlas texture and points ImGui's font id at it.
	/// </summary>
	public static ImGuiPainter Create(IEngine engine)
	{
		ArgumentNullException.ThrowIfNull(engine);

		// A list-push, alpha-over (straight, not premultiplied — ImGui's convention) textured technique. The list-push
		// block carries the pixel→clip scale/translate; the per-command tag slot is unused (ImGui has one primitive).
		engine.DefineTechnique(Technique, new TechniqueDescriptor(Technique, HasVertexInput: true,
			Blend: BlendMode.AlphaOver, LinearFilter: true, Layout: VertexLayout, HasListPush: true));

		var io = Im.GetIO();
		io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out var width, out var height, out _);
		var bytes = new byte[(long)width * height * 4];
		System.Runtime.InteropServices.Marshal.Copy(pixels, bytes, 0, bytes.Length);
		// The atlas is linear coverage data, not sRGB — upload UNORM, no mips (it is sampled 1:1 at native resolution).
		var font = engine.CreateTexture(TextureData.FromRgba8(width, height, bytes, TextureFormat.Rgba8Unorm, generateMips: false));

		io.Fonts.SetTexID((IntPtr)1); // a non-null id; every ImGui draw binds the one font atlas
		return new ImGuiPainter(font, io.Fonts.TexUvWhitePixel);
	}

	/// <summary>Preview these graph resources (<see cref="IEngine.InspectableResources"/> ids) as a thumbnail grid in
	/// the next <see cref="Draw"/> (the resource viewer). Pass an empty span to show none. Overwritten each frame.</summary>
	public void SetThumbnails(ReadOnlySpan<int> resourceIds)
	{
		if (_thumbnails.Length < resourceIds.Length)
			_thumbnails = new int[resourceIds.Length];
		resourceIds.CopyTo(_thumbnails);
		_thumbnailCount = resourceIds.Length;
	}

	/// <summary>Overlay these line segments (each <c>(x0,y0,x1,y1)</c> in framebuffer pixels) in the next
	/// <see cref="Draw"/> — the detached-camera frustum-freeze wireframe. Pass an empty span to clear. Overwritten each frame.</summary>
	public void SetDebugLines(ReadOnlySpan<Vector4> segments)
	{
		if (_lines.Length < segments.Length)
			_lines = new Vector4[segments.Length];
		segments.CopyTo(_lines);
		_lineCount = segments.Length;
	}

	/// <summary>
	/// Submit this frame's ImGui draw data — and any requested resource-viewer thumbnails and debug lines — into the
	/// current pass. The host must have called <c>ImGui.Render()</c> earlier this frame. A no-op when there is nothing
	/// to draw. Call from a pass that blends the target (the "hud" pass).
	/// </summary>
	public void Draw(IRenderContext ctx)
	{
		ArgumentNullException.ThrowIfNull(ctx);
		var displaySize = Im.GetIO().DisplaySize;
		if (displaySize.X <= 0 || displaySize.Y <= 0)
			return;

		DrawUI(ctx, displaySize);
		DrawThumbnails(ctx, displaySize);
		DrawLines(ctx, displaySize);
	}

	private unsafe void DrawUI(IRenderContext ctx, Vector2 displaySize)
	{
		var drawData = Im.GetDrawData();
		if (drawData.NativePtr is null || drawData.CmdListsCount == 0 || drawData.TotalVtxCount == 0)
			return;

		EnsureScratch(drawData.TotalVtxCount, drawData.TotalIdxCount);
		var cmdCount = 0;
		for (var n = 0; n < drawData.CmdListsCount; n++)
			cmdCount += drawData.CmdLists[n].CmdBuffer.Size;
		EnsureCommands(cmdCount);

		var clipOff = drawData.DisplayPos;
		var clipScale = drawData.FramebufferScale;
		if (clipScale.X <= 0 || clipScale.Y <= 0)
			clipScale = Vector2.One; // host didn't set DisplayFramebufferScale — assume 1:1

		var vtxBase = 0;
		var idxBase = 0;
		var cmd = 0;
		fixed (ImGuiVertex* vDst = _verts)
		fixed (ushort* iDst = _indices)
		{
			for (var n = 0; n < drawData.CmdListsCount; n++)
			{
				var list = drawData.CmdLists[n];
				var vCount = list.VtxBuffer.Size;
				var iCount = list.IdxBuffer.Size;
				Buffer.MemoryCopy((void*)list.VtxBuffer.Data, vDst + vtxBase, (long)(_verts.Length - vtxBase) * sizeof(ImGuiVertex), (long)vCount * sizeof(ImGuiVertex));
				Buffer.MemoryCopy((void*)list.IdxBuffer.Data, iDst + idxBase, (long)(_indices.Length - idxBase) * sizeof(ushort), (long)iCount * sizeof(ushort));
				for (var c = 0; c < list.CmdBuffer.Size; c++)
				{
					var pcmd = list.CmdBuffer[c];
					// Clip rect in framebuffer pixels; the backend clamps it to the target. ImGui's +Y-down origin
					// matches the clip space the draw-list verb sets up, so no Y-flip.
					var x = (pcmd.ClipRect.X - clipOff.X) * clipScale.X;
					var y = (pcmd.ClipRect.Y - clipOff.Y) * clipScale.Y;
					var w = (pcmd.ClipRect.Z - pcmd.ClipRect.X) * clipScale.X;
					var h = (pcmd.ClipRect.W - pcmd.ClipRect.Y) * clipScale.Y;
					_commands[cmd++] = new DrawCommand(new Rect(x, y, w, h), 0,
						idxBase + (int)pcmd.IdxOffset, (int)pcmd.ElemCount, vtxBase + (int)pcmd.VtxOffset, 0);
				}
				vtxBase += vCount;
				idxBase += iCount;
			}
		}

		var sx = 2f / displaySize.X;
		var sy = 2f / displaySize.Y;
		ReadOnlySpan<float> push = [sx, sy, -1f - clipOff.X * sx, -1f - clipOff.Y * sy];
		ReadOnlySpan<TextureRef> textures = [_font];
		ctx.DrawList(Technique, DrawListIdentity.Transient, textures, _verts.AsSpan(0, vtxBase), _indices.AsSpan(0, idxBase), _commands.AsSpan(0, cmd), push);
	}

	private void DrawThumbnails(IRenderContext ctx, Vector2 displaySize)
	{
		if (_thumbnailCount == 0)
			return;

		if (_thumbTextures.Length < _thumbnailCount)
			_thumbTextures = new TextureRef[_thumbnailCount];
		var count = 0;
		for (var i = 0; i < _thumbnailCount; i++)
			if (ctx.ResolveInspectable(_thumbnails[i]) is { } texture)
				_thumbTextures[count++] = texture;
		if (count == 0)
			return;

		EnsureScratch(count * 4, count * 6);
		EnsureCommands(count);
		var cols = Math.Max(1, (int)((displaySize.X - ThumbX - 10f) / (ThumbW + ThumbGap)));
		for (var i = 0; i < count; i++)
		{
			var gx = ThumbX + i % cols * (ThumbW + ThumbGap);
			var gy = ThumbY + i / cols * (ThumbH + ThumbGap);
			Quad(i, gx, gy, ThumbW, ThumbH, new Vector2(0, 0), new Vector2(1, 1), White);
			// One run per thumbnail: it samples its own resolved resource texture (textures[i]).
			_commands[i] = new DrawCommand(new Rect(0, 0, displaySize.X, displaySize.Y), i, i * 6, 6, 0, 0);
		}

		ReadOnlySpan<float> push = [2f / displaySize.X, 2f / displaySize.Y, -1f, -1f];
		ctx.DrawList(Technique, DrawListIdentity.Transient, _thumbTextures.AsSpan(0, count), _verts.AsSpan(0, count * 4), _indices.AsSpan(0, count * 6), _commands.AsSpan(0, count), push);
	}

	private void DrawLines(IRenderContext ctx, Vector2 displaySize)
	{
		if (_lineCount == 0)
			return;

		EnsureScratch(_lineCount * 4, _lineCount * 6);
		EnsureCommands(1);
		var half = MathF.Max(0.5f, LineThickness * 0.5f);
		for (var i = 0; i < _lineCount; i++)
		{
			var s = _lines[i];
			var a = new Vector2(s.X, s.Y);
			var b = new Vector2(s.Z, s.W);
			var dir = b - a;
			var len = dir.Length();
			var perp = len > 1e-4f ? new Vector2(-dir.Y, dir.X) / len * half : new Vector2(0, half);
			var v = i * 4;
			_verts[v + 0] = new ImGuiVertex(a - perp, _whiteUv, LineColor);
			_verts[v + 1] = new ImGuiVertex(b - perp, _whiteUv, LineColor);
			_verts[v + 2] = new ImGuiVertex(b + perp, _whiteUv, LineColor);
			_verts[v + 3] = new ImGuiVertex(a + perp, _whiteUv, LineColor);
			Tri(i);
		}

		// All segments share the font atlas (the white texel) and one scissor — a single run.
		_commands[0] = new DrawCommand(new Rect(0, 0, displaySize.X, displaySize.Y), 0, 0, _lineCount * 6, 0, 0);
		ReadOnlySpan<float> push = [2f / displaySize.X, 2f / displaySize.Y, -1f, -1f];
		ReadOnlySpan<TextureRef> textures = [_font];
		ctx.DrawList(Technique, DrawListIdentity.Transient, textures, _verts.AsSpan(0, _lineCount * 4), _indices.AsSpan(0, _lineCount * 6), _commands.AsSpan(0, 1), push);
	}

	// Write quad i (4 verts) at pixel rect (x,y,w,h) spanning uv0..uv1, plus its two triangles.
	private void Quad(int i, float x, float y, float w, float h, Vector2 uv0, Vector2 uv1, uint color)
	{
		var v = i * 4;
		_verts[v + 0] = new ImGuiVertex(new Vector2(x, y), uv0, color);
		_verts[v + 1] = new ImGuiVertex(new Vector2(x + w, y), new Vector2(uv1.X, uv0.Y), color);
		_verts[v + 2] = new ImGuiVertex(new Vector2(x + w, y + h), uv1, color);
		_verts[v + 3] = new ImGuiVertex(new Vector2(x, y + h), new Vector2(uv0.X, uv1.Y), color);
		Tri(i);
	}

	// Write the two triangles (6 indices) of quad i.
	private void Tri(int i)
	{
		var ix = i * 6;
		var b = (ushort)(i * 4);
		_indices[ix + 0] = b;
		_indices[ix + 1] = (ushort)(b + 1);
		_indices[ix + 2] = (ushort)(b + 2);
		_indices[ix + 3] = b;
		_indices[ix + 4] = (ushort)(b + 2);
		_indices[ix + 5] = (ushort)(b + 3);
	}

	private void EnsureScratch(int vertices, int indices)
	{
		if (_verts.Length < vertices)
			_verts = new ImGuiVertex[Math.Max(vertices, _verts.Length + _verts.Length / 2)];
		if (_indices.Length < indices)
			_indices = new ushort[Math.Max(indices, _indices.Length + _indices.Length / 2)];
	}

	private void EnsureCommands(int count)
	{
		if (_commands.Length < count)
			_commands = new DrawCommand[Math.Max(count, _commands.Length + _commands.Length / 2)];
	}
}
