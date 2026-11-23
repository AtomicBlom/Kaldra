using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Loom.Core.Primitives;
using Vysara.Drawing;

using LoomDrawCommand = Loom.Core.Hosting.DrawCommand;
using LoomOverlayVertex = Loom.Core.Geometry.OverlayVertex;
using VysaraDrawCommand = Vysara.Drawing.DrawCommand;
using VysaraOverlayVertex = Vysara.Drawing.OverlayVertex;

namespace Loom.Tests;

/// <summary>
/// The draw contract between Vysara and Loom is two pairs of structs that must be byte-identical: Vysara's
/// <see cref="VysaraOverlayVertex"/> and <see cref="VysaraDrawCommand"/>, and Loom's <see cref="LoomOverlayVertex"/>
/// and <see cref="LoomDrawCommand"/>. <c>UIPainter</c> hands Vysara's spans to the backend through
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
		Unsafe.SizeOf<VysaraOverlayVertex>().ShouldBe(Unsafe.SizeOf<LoomOverlayVertex>());

		ReadOnlySpan<VysaraOverlayVertex> vysara =
		[
			new(new Vector2(1.5f, 2.5f), new Vector2(3.5f, 4.5f),
				new PremultipliedHdrColor((Half)0.25f, (Half)0.5f, (Half)0.75f, (Half)1f),
				new Vector4(5.5f, 6.5f, 7.5f, 8.5f)),
		];
		var loom = MemoryMarshal.Cast<VysaraOverlayVertex, LoomOverlayVertex>(vysara);

		loom.Length.ShouldBe(1);
		loom[0].Position.ShouldBe(vysara[0].Position);
		loom[0].Uv.ShouldBe(vysara[0].Uv);
		loom[0].Color.ShouldBe(new Half4(vysara[0].Color.R, vysara[0].Color.G, vysara[0].Color.B, vysara[0].Color.A));
		loom[0].Custom.ShouldBe(vysara[0].Custom);
	}

	[Test]
	public void DrawCommand_is_byte_identical_on_both_sides()
	{
		Unsafe.SizeOf<VysaraDrawCommand>().ShouldBe(Unsafe.SizeOf<LoomDrawCommand>());

		ReadOnlySpan<VysaraDrawCommand> vysara =
		[
			new(new Vysara.Rect(1.5f, 2.5f, 3.5f, 4.5f), TextureIndex: 5, IndexOffset: 6, IndexCount: 7, VertexOffset: 8, Tag: 9u),
		];
		var loom = MemoryMarshal.Cast<VysaraDrawCommand, LoomDrawCommand>(vysara);

		loom.Length.ShouldBe(1);
		var (clip, expected) = (loom[0].ClipRect, vysara[0].ClipRect);
		(clip.X, clip.Y, clip.Width, clip.Height).ShouldBe((expected.X, expected.Y, expected.Width, expected.Height));
		loom[0].TextureIndex.ShouldBe(vysara[0].TextureIndex);
		loom[0].IndexOffset.ShouldBe(vysara[0].IndexOffset);
		loom[0].IndexCount.ShouldBe(vysara[0].IndexCount);
		loom[0].VertexOffset.ShouldBe(vysara[0].VertexOffset);
		loom[0].Tag.ShouldBe(vysara[0].Tag);
	}
}
