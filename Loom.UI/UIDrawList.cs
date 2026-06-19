using System.Numerics;
using Loom.Core;

namespace Loom.UI;

/// <summary>
/// A pooled builder for the neutral draw stream — the analog of ImGui's <c>ImDrawList</c> (PlanningUI.md §D2.5).
/// Bakes the composed transform into vertex positions and merges consecutive primitives that share
/// <c>(kind, textureId, clip)</c> into one indexed draw command (atlas-everything + state-run batching, §D2.6).
/// </summary>
internal sealed class UIDrawList
{
	private readonly List<UIVertex> _verts = [];
	private readonly List<ushort> _indices = [];
	private readonly List<UIDrawCommand> _commands = [];
	private readonly Vector2 _surfaceSize;

	// Open run state — extended while (kind, tex, clip) match; flushed into a command otherwise.
	private bool _open;
	private UIPrimKind _runKind;
	private int _runTex;
	private Rect _runClip;
	private int _runIdxStart;
	private int _runIdxCount;

	public UIDrawList(Vector2 surfaceSize) => _surfaceSize = surfaceSize;

	/// <summary>Append a transformed quad (the corners are baked through <paramref name="matrix"/>).</summary>
	public void AddRect(in Rect local, in Matrix3x2 matrix, Half4 color, UIPrimKind kind, Vector4 @params, in Rect clip, int textureId = 0)
	{
		if (clip.IsEmpty)
			return;
		EnsureRun(kind, textureId, clip);

		var baseV = (ushort)_verts.Count;
		AddVertex(new Vector2(local.X, local.Y), new Vector2(0, 0), matrix, color, @params);
		AddVertex(new Vector2(local.Right, local.Y), new Vector2(1, 0), matrix, color, @params);
		AddVertex(new Vector2(local.Right, local.Bottom), new Vector2(1, 1), matrix, color, @params);
		AddVertex(new Vector2(local.X, local.Bottom), new Vector2(0, 1), matrix, color, @params);

		_indices.Add(baseV); _indices.Add((ushort)(baseV + 1)); _indices.Add((ushort)(baseV + 2));
		_indices.Add(baseV); _indices.Add((ushort)(baseV + 2)); _indices.Add((ushort)(baseV + 3));
		_runIdxCount += 6;
	}

	private void AddVertex(Vector2 local, Vector2 uv, in Matrix3x2 matrix, Half4 color, Vector4 @params) =>
		_verts.Add(new UIVertex(Vector2.Transform(local, matrix), uv, color, @params));

	private void EnsureRun(UIPrimKind kind, int tex, in Rect clip)
	{
		if (_open && _runKind == kind && _runTex == tex && _runClip.Equals(clip))
			return;
		Flush();
		_open = true;
		_runKind = kind;
		_runTex = tex;
		_runClip = clip;
		_runIdxStart = _indices.Count;
		_runIdxCount = 0;
	}

	private void Flush()
	{
		if (_open && _runIdxCount > 0)
			_commands.Add(new UIDrawCommand(_runKind, _runTex, _runClip, _runIdxStart, _runIdxCount, 0));
		_open = false;
	}

	public UIDrawData Build(bool hdr, uint revision)
	{
		Flush();
		return new UIDrawData
		{
			Vertices = _verts.ToArray(),
			Indices = _indices.ToArray(),
			Commands = _commands.ToArray(),
			SurfaceSize = _surfaceSize,
			IsHdrSurface = hdr,
			Revision = revision,
		};
	}
}
