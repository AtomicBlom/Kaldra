using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using BinaryVibrance.Kaldra.Core.Primitives;
using BinaryVibrance.Vysara.Drawing;

using KaldraDrawCommand = BinaryVibrance.Kaldra.Core.Hosting.DrawCommand;
using KaldraOverlayVertex = BinaryVibrance.Kaldra.Core.Geometry.OverlayVertex;
using VysaraDrawCommand = BinaryVibrance.Vysara.Drawing.DrawCommand;
using VysaraOverlayVertex = BinaryVibrance.Vysara.Drawing.OverlayVertex;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// The draw contract between Vysara and Kaldra is two pairs of structs that must be byte-identical: Vysara's
/// <see cref="VysaraOverlayVertex"/> and <see cref="VysaraDrawCommand"/>, and Kaldra's <see cref="KaldraOverlayVertex"/>
/// and <see cref="KaldraDrawCommand"/>. <c>UIPainter</c> hands Vysara's spans to the backend through
/// <see cref="MemoryMarshal.Cast{TFrom, TTo}(ReadOnlySpan{TFrom})"/>, which checks nothing: a field reordered, widened
/// or retyped on one side compiles and runs, and draws garbage. The two live in different repositories, so nothing
/// else keeps them in step.
/// <para>
/// Each test writes a distinct value into every field on one side, reinterprets the bytes as the other side exactly as
/// the painter does, and reads every field back. A field at a different offset, or of a different type, reads back a
/// different value; a size mismatch fails on its own. Comparing values rather than reflected offsets keeps the check
/// meaningful under NativeAOT, where layout reflection is not available.
/// </para>
/// </summary>
public class DrawContractLayoutTests
{
	[Test]
	public void OverlayVertex_is_byte_identical_on_both_sides()
	{
		Unsafe.SizeOf<VysaraOverlayVertex>().ShouldBe(Unsafe.SizeOf<KaldraOverlayVertex>());

		ReadOnlySpan<VysaraOverlayVertex> vysara =
		[
			new(new Vector2(1.5f, 2.5f), new Vector2(3.5f, 4.5f),
				new PremultipliedHdrColor((Half)0.25f, (Half)0.5f, (Half)0.75f, (Half)1f),
				new Vector4(5.5f, 6.5f, 7.5f, 8.5f)),
		];
		var kaldra = MemoryMarshal.Cast<VysaraOverlayVertex, KaldraOverlayVertex>(vysara);

		kaldra.Length.ShouldBe(1);
		kaldra[0].Position.ShouldBe(vysara[0].Position);
		kaldra[0].Uv.ShouldBe(vysara[0].Uv);
		kaldra[0].Color.ShouldBe(new Half4(vysara[0].Color.R, vysara[0].Color.G, vysara[0].Color.B, vysara[0].Color.A));
		kaldra[0].Custom.ShouldBe(vysara[0].Custom);
	}

	[Test]
	public void DrawCommand_is_byte_identical_on_both_sides()
	{
		Unsafe.SizeOf<VysaraDrawCommand>().ShouldBe(Unsafe.SizeOf<KaldraDrawCommand>());

		ReadOnlySpan<VysaraDrawCommand> vysara =
		[
			new(new BinaryVibrance.Vysara.Rect(1.5f, 2.5f, 3.5f, 4.5f), TextureIndex: 5, IndexOffset: 6, IndexCount: 7, VertexOffset: 8, Tag: 9u),
		];
		var kaldra = MemoryMarshal.Cast<VysaraDrawCommand, KaldraDrawCommand>(vysara);

		kaldra.Length.ShouldBe(1);
		var (clip, expected) = (kaldra[0].ClipRect, vysara[0].ClipRect);
		(clip.X, clip.Y, clip.Width, clip.Height).ShouldBe((expected.X, expected.Y, expected.Width, expected.Height));
		kaldra[0].TextureIndex.ShouldBe(vysara[0].TextureIndex);
		kaldra[0].IndexOffset.ShouldBe(vysara[0].IndexOffset);
		kaldra[0].IndexCount.ShouldBe(vysara[0].IndexCount);
		kaldra[0].VertexOffset.ShouldBe(vysara[0].VertexOffset);
		kaldra[0].Tag.ShouldBe(vysara[0].Tag);
	}
}
