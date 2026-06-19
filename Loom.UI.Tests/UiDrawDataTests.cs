using System.Numerics;

using Loom.Core;

namespace Loom.UI.Tests;

/// <summary>
/// MU0 Phase A coverage for the neutral UI draw-data contract (PlanningUI.md §D8.1). <see cref="UIDrawData.Dump"/>
/// is the "golden draw-list" — the UI analog of asserting <c>BarrierPlan</c>/<c>AliasPlan</c>: a deterministic,
/// CPU-only text snapshot of the whole vertex/command stream, so the compositor pipeline up to GPU upload is
/// tested with no backend. These tests pin its format + determinism and the premultiplied-colour contract; the
/// DP-precedence, layout, and compositor tests arrive with MU0 Phase B.
/// </summary>
public sealed class UIDrawDataTests
{
	[Test]
	public void Empty_draw_data_dumps_a_stable_deterministic_snapshot()
	{
		const string nl = "\n";
		var expected =
			"UIDrawData (rev 0, surface 0x0, hdr=False)" + nl +
			"  Vertices (0):" + nl +
			"  Indices (0):" + nl +
			"  Commands (0):" + nl;

		UIDrawData.Empty.Dump().ReplaceLineEndings("\n").ShouldBe(expected);
		// Deterministic: the same instance dumps identically every call (feeds the no-op/Revision diffing).
		UIDrawData.Empty.Dump().ShouldBe(UIDrawData.Empty.Dump());
	}

	[Test]
	public void A_built_draw_stream_dumps_the_expected_golden_list()
	{
		var draw = BuildQuad(revision: 7);
		var dump = draw.Dump().ReplaceLineEndings("\n");

		dump.ShouldContain("UIDrawData (rev 7, surface 1920x1080, hdr=False)");
		dump.ShouldContain("Vertices (4):");
		// State-run batching collapses the quad to one index run; the index stream dumps in submission order.
		dump.ShouldContain("Indices (6): 0 1 2 0 2 3");
		dump.ShouldContain("Commands (1):");
		dump.ShouldContain("[0] Fill tex=0 clip=(0,0 10x20) idx=0+6 vtx=0");
		// The first vertex carries its premultiplied colour + position (red, opaque, top-left).
		dump.ShouldContain("[0] pos=(0,0) uv=(0,0) col=(1,0,0,1)");
	}

	[Test]
	public void Dump_is_deterministic_across_identical_builds() =>
		BuildQuad(revision: 3).Dump().ShouldBe(BuildQuad(revision: 3).Dump());

	[Test]
	public void Half4_FromStraight_premultiplies_rgb_by_alpha()
	{
		// Straight (1, 0.5, 0.25) at alpha 0.5 → premultiplied (0.5, 0.25, 0.125, 0.5). All exactly representable
		// in Half (powers of two), so the round-trip is exact — the premultiplied-RGBA16F authoring contract.
		var c = Half4.FromStraight(1f, 0.5f, 0.25f, 0.5f);
		((float)c.R).ShouldBe(0.5f);
		((float)c.G).ShouldBe(0.25f);
		((float)c.B).ShouldBe(0.125f);
		((float)c.A).ShouldBe(0.5f);
	}

	[Test]
	public void Rect_intersect_clips_to_the_overlap_and_reports_disjoint_as_empty()
	{
		new Rect(0, 0, 100, 100).Intersect(new Rect(50, 40, 100, 100)).ShouldBe(new Rect(50, 40, 50, 60));
		new Rect(0, 0, 10, 10).Intersect(new Rect(20, 20, 5, 5)).IsEmpty.ShouldBeTrue();
	}

	// A unit quad as one Fill draw: 4 verts, 6 indices, 1 command — the minimal non-empty draw stream.
	private static UIDrawData BuildQuad(uint revision)
	{
		var red = Half4.FromStraight(1f, 0f, 0f, 1f);
		var verts = new UIVertex[]
		{
			new(new Vector2(0, 0), new Vector2(0, 0), red, Vector4.Zero),
			new(new Vector2(10, 0), new Vector2(1, 0), red, Vector4.Zero),
			new(new Vector2(10, 20), new Vector2(1, 1), red, Vector4.Zero),
			new(new Vector2(0, 20), new Vector2(0, 1), red, Vector4.Zero),
		};
		var indices = new ushort[] { 0, 1, 2, 0, 2, 3 };
		var commands = new UIDrawCommand[] { new(UIPrimKind.Fill, 0, new Rect(0, 0, 10, 20), 0, 6, 0) };
		return new UIDrawData
		{
			Vertices = verts,
			Indices = indices,
			Commands = commands,
			SurfaceSize = new Vector2(1920, 1080),
			Revision = revision,
		};
	}
}
