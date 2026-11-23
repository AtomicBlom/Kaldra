using System.Numerics;

namespace BinaryVibrance.Kaldra.Assets;

/// <summary>
/// A grid sprite-sheet / texture atlas: divides a texture into <see cref="Columns"/>×<see cref="Rows"/>
/// equal cells and yields each cell's normalised uv-rect — <c>(u0, v0, u1, v1)</c> in [0,1] texture space — to
/// drop into a sprite instance's uv-rect. Cell index is row-major (left-to-right, top-to-bottom), which is
/// also the natural order for a sprite-sheet animation's frames.
/// <para>
/// Neutral, like the rest of <see cref="BinaryVibrance.Kaldra.Assets"/> — pure uv math over a texture the caller loaded/generated
/// separately (no graphics-API type, no upload). An optional half-texel <c>inset</c> pulls each rect in from the
/// cell edge so nearest/linear sampling never bleeds a neighbouring cell at the seam.
/// </para>
/// </summary>
public sealed class SpriteSheet
{
	private readonly float _cellW;
	private readonly float _cellH;
	private readonly float _insetU;
	private readonly float _insetV;

	/// <summary>Columns in the grid (cells across).</summary>
	public int Columns { get; }

	/// <summary>Rows in the grid (cells down).</summary>
	public int Rows { get; }

	/// <summary>Total cells — <see cref="Columns"/> × <see cref="Rows"/>.</summary>
	public int Count => Columns * Rows;

	/// <summary>
	/// Describe a <paramref name="columns"/>×<paramref name="rows"/> grid. Supply the atlas pixel size
	/// (<paramref name="atlasWidth"/>/<paramref name="atlasHeight"/>) and a non-zero <paramref name="insetTexels"/>
	/// to inset each cell's uv-rect by that many texels (a half texel, 0.5, is the usual anti-bleed guard);
	/// leave them zero for exact cell rects.
	/// </summary>
	public SpriteSheet(int columns, int rows, int atlasWidth = 0, int atlasHeight = 0, float insetTexels = 0f)
	{
		if (columns <= 0 || rows <= 0)
			throw new ArgumentOutOfRangeException(nameof(columns), "A sprite-sheet grid needs at least one column and one row.");
		Columns = columns;
		Rows = rows;
		_cellW = 1f / columns;
		_cellH = 1f / rows;
		_insetU = atlasWidth > 0 ? insetTexels / atlasWidth : 0f;
		_insetV = atlasHeight > 0 ? insetTexels / atlasHeight : 0f;
	}

	/// <summary>The uv-rect of cell <paramref name="index"/> (row-major). Wraps modulo <see cref="Count"/> so an
	/// ever-increasing animation frame counter indexes safely.</summary>
	public Vector4 Uv(int index)
	{
		var cell = ((index % Count) + Count) % Count; // wrap, tolerating negatives
		return Uv(cell % Columns, cell / Columns);
	}

	/// <summary>The uv-rect <c>(u0, v0, u1, v1)</c> of the cell at <paramref name="column"/>, <paramref name="row"/>.</summary>
	public Vector4 Uv(int column, int row) => new(
		column * _cellW + _insetU,
		row * _cellH + _insetV,
		(column + 1) * _cellW - _insetU,
		(row + 1) * _cellH - _insetV);
}
