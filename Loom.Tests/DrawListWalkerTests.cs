using Loom.Core.Geometry;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Core.Primitives;

namespace Loom.Tests;

/// <summary>
/// The neutral draw-list decoder every backend's <c>DrawList</c> now drives: the group-opacity layer nesting, its cap
/// and the symmetric suppression, the scissor, and the per-command texture memoisation.
/// <para>
/// This is the piece three backends share, so it is worth pinning device-free and in detail — a mistake here is a
/// mistake on Vulkan, DirectX 12 and WebGPU at once. The layer state machine in particular used to be three separate
/// implementations, and its interesting cases (an unbalanced marker, a subtree past the cap) are ones no demo produces
/// and no golden would catch.
/// </para>
/// </summary>
public class DrawListWalkerTests
{
	private const int Target = 100;

	private static readonly Texture[] Textures = [new Texture(7, 4, 4), new Texture(9, 4, 4)];

	private static DrawCommand Draw(int textureIndex = 0, int indexOffset = 0, int indexCount = 3, uint tag = 1) =>
		new(new Rect(0f, 0f, Target, Target), textureIndex, indexOffset, indexCount, 0, tag);

	private static DrawCommand Marker(uint tag, float x = 0f, float y = 0f, float w = 50f, float h = 50f, int vertexOffset = 0) =>
		new(new Rect(x, y, w, h), 0, 0, 0, vertexOffset, tag);

	// A tint per marker vertex, so a BeginLayer's colour is attributable to the marker that carried it.
	private static OverlayVertex[] Overlay(params float[] alphas)
	{
		var v = new OverlayVertex[alphas.Length];
		for (var i = 0; i < alphas.Length; i++)
			v[i] = new OverlayVertex(default, default, Half4.FromStraight(1f, 1f, 1f, alphas[i]), default);
		return v;
	}

	private static DrawListWalker Walk(DrawCommand[] commands, OverlayVertex[]? overlay = null, bool layersSupported = true) =>
		new(commands, Textures, overlay ?? [], layersSupported, Target, Target, Target, Target);

	[Test]
	public void A_run_of_commands_sharing_a_texture_binds_it_once()
	{
		// What the memoisation is for: a UI list is mostly the glyph atlas for run after run, and on the desktop
		// backends a rebind costs a descriptor write per command.
		var walker = Walk([Draw(0), Draw(0), Draw(1), Draw(1), Draw(0)]);
		var bound = new List<(int Texture, bool Changed)>();
		while (walker.MoveNext())
			bound.Add((walker.TextureId, walker.TextureChanged));

		bound.ShouldBe([(7, true), (7, false), (9, true), (9, false), (7, true)]);
	}

	[Test]
	public void A_texture_index_past_the_bound_set_clamps()
	{
		// The producer indexes into the submission's textures; a bad index must pick an existing one rather than throw
		// from inside a command stream.
		var walker = Walk([Draw(textureIndex: 99)]);
		walker.MoveNext().ShouldBeTrue();
		walker.TextureId.ShouldBe(9);
	}

	[Test]
	public void A_fully_clipped_command_is_not_reported()
	{
		// Skipped in the decoder so no backend has to remember to check, and so none can hand a negative extent to an
		// unsigned scissor API.
		var offscreen = new DrawCommand(new Rect(500f, 500f, 10f, 10f), 0, 0, 3, 0, 1);
		var walker = Walk([offscreen, Draw(), offscreen]);
		var steps = 0;
		while (walker.MoveNext())
			steps++;
		steps.ShouldBe(1);
	}

	[Test]
	public void Layer_markers_decode_to_a_matched_begin_and_end_at_one_depth()
	{
		var walker = Walk(
			[Marker(DrawListGeometry.TagBeginLayer, vertexOffset: 0), Draw(), Marker(DrawListGeometry.TagEndLayer)],
			Overlay(0.5f));

		walker.MoveNext().ShouldBeTrue();
		walker.Step.ShouldBe(DrawListStep.BeginLayer);
		walker.Depth.ShouldBe(0);
		((float)walker.Tint.A).ShouldBe(0.5f, 0.01);

		walker.MoveNext().ShouldBeTrue();
		walker.Step.ShouldBe(DrawListStep.Draw);

		walker.MoveNext().ShouldBeTrue();
		walker.Step.ShouldBe(DrawListStep.EndLayer);
		walker.Depth.ShouldBe(0);
		// The tint and bounds come back from the BeginLayer that opened it — the backend needs them to composite, and
		// holding them here is what lets a backend drop its own parallel layer stack.
		((float)walker.Tint.A).ShouldBe(0.5f, 0.01);

		walker.MoveNext().ShouldBeFalse();
		walker.OpenLayers.ShouldBe(0);
	}

	[Test]
	public void Nested_layers_report_increasing_then_decreasing_depths_with_their_own_tints()
	{
		var walker = Walk(
			[
				Marker(DrawListGeometry.TagBeginLayer, vertexOffset: 0),
				Marker(DrawListGeometry.TagBeginLayer, vertexOffset: 1),
				Draw(),
				Marker(DrawListGeometry.TagEndLayer),
				Marker(DrawListGeometry.TagEndLayer),
			],
			Overlay(0.25f, 0.75f));

		var seen = new List<(DrawListStep Step, int Depth, int Alpha)>();
		while (walker.MoveNext())
			seen.Add((walker.Step, walker.Depth, (int)MathF.Round((float)walker.Tint.A * 100f)));

		// The inner layer closes first and each one composites with the tint it was opened with — swapping those is how
		// a nested group ends up faded by the wrong alpha.
		seen.ShouldBe(
		[
			(DrawListStep.BeginLayer, 0, 25),
			(DrawListStep.BeginLayer, 1, 75),
			(DrawListStep.Draw, 1, 75),
			(DrawListStep.EndLayer, 1, 75),
			(DrawListStep.EndLayer, 0, 25),
		]);
	}

	[Test]
	public void A_layer_composite_invalidates_the_memoised_texture()
	{
		// The composite binds the layer as its own texture, so the next draw has to rebind even though its texture never
		// changed. Missing this samples the layer for a glyph.
		var walker = Walk(
			[
				Draw(0),
				Marker(DrawListGeometry.TagBeginLayer),
				Marker(DrawListGeometry.TagEndLayer),
				Draw(0),
			],
			Overlay(1f));

		var changes = new List<bool>();
		while (walker.MoveNext())
			if (walker.Step == DrawListStep.Draw)
				changes.Add(walker.TextureChanged);

		changes.ShouldBe([true, true]);
	}

	[Test]
	public void A_subtree_past_the_nesting_cap_is_suppressed_symmetrically()
	{
		// The bug this exists to prevent: suppressing a BeginLayer without its EndLayer pops an enclosing group early,
		// so every layer after it composites into the wrong parent. Every reported depth must still pair up.
		var commands = new List<DrawCommand>();
		var overDeep = DrawListGeometry.MaxLayerNesting + 2;
		for (var i = 0; i < overDeep; i++)
			commands.Add(Marker(DrawListGeometry.TagBeginLayer, vertexOffset: i));
		commands.Add(Draw());
		for (var i = 0; i < overDeep; i++)
			commands.Add(Marker(DrawListGeometry.TagEndLayer));

		var walker = Walk([.. commands], Overlay([.. Enumerable.Repeat(1f, overDeep)]));
		var opened = new List<int>();
		var closed = new List<int>();
		while (walker.MoveNext())
		{
			if (walker.Step == DrawListStep.BeginLayer)
				opened.Add(walker.Depth);
			else if (walker.Step == DrawListStep.EndLayer)
				closed.Add(walker.Depth);
		}

		opened.ShouldBe([.. Enumerable.Range(0, DrawListGeometry.MaxLayerNesting)]);
		// Closed in reverse, and exactly the depths that were opened — nothing shifted.
		closed.ShouldBe([.. Enumerable.Range(0, DrawListGeometry.MaxLayerNesting).Reverse()]);
		walker.OpenLayers.ShouldBe(0);
		walker.NestingExceeded.ShouldBeTrue();
		walker.DeepestNesting.ShouldBe(overDeep);
	}

	[Test]
	public void An_unmatched_end_layer_is_ignored()
	{
		// A malformed list must not pop a layer that was never opened, which on a backend means ending a scope the pass
		// owns.
		var walker = Walk([Marker(DrawListGeometry.TagEndLayer), Draw()], Overlay(1f));
		var steps = new List<DrawListStep>();
		while (walker.MoveNext())
			steps.Add(walker.Step);

		steps.ShouldBe([DrawListStep.Draw]);
	}

	[Test]
	public void An_unmatched_begin_layer_is_left_open_for_the_backend_to_unwind()
	{
		var walker = Walk([Marker(DrawListGeometry.TagBeginLayer), Marker(DrawListGeometry.TagBeginLayer, vertexOffset: 1), Draw()], Overlay(1f, 1f));
		while (walker.MoveNext()) { }

		walker.OpenLayers.ShouldBe(2);
		// Unwound innermost-first, so a backend that restores a scope per level restores them in the right order.
		walker.TryUnwind(out var first).ShouldBeTrue();
		first.ShouldBe(1);
		walker.TryUnwind(out var second).ShouldBeTrue();
		second.ShouldBe(0);
		walker.TryUnwind(out _).ShouldBeFalse();
	}

	[Test]
	public void Without_layer_support_the_markers_decode_as_ordinary_draws()
	{
		// A non-overlay vertex list, or a pass with no scope to suspend: the markers carry no indices, so drawing them
		// is a no-op — but they must not be silently dropped either, because that is the backend's decision to make.
		var walker = Walk(
			[Marker(DrawListGeometry.TagBeginLayer), Draw(), Marker(DrawListGeometry.TagEndLayer)],
			layersSupported: false);

		var steps = new List<DrawListStep>();
		while (walker.MoveNext())
			steps.Add(walker.Step);

		steps.ShouldBe([DrawListStep.Draw, DrawListStep.Draw, DrawListStep.Draw]);
		walker.OpenLayers.ShouldBe(0);
	}

	[Test]
	public void A_layer_area_scales_the_bounds_from_surface_space_to_the_target()
	{
		// A pass rendering at a different resolution than the list authors in: the walker owns the conversion so a
		// backend narrowing a clear or render area does not have to redo it.
		var commands = new[] { Marker(DrawListGeometry.TagBeginLayer, x: 10f, y: 10f, w: 20f, h: 20f) };
		var walker = new DrawListWalker(commands, Textures, Overlay(1f), layersSupported: true,
			targetWidth: 200, targetHeight: 200, surfaceWidth: 100f, surfaceHeight: 100f);

		walker.MoveNext().ShouldBeTrue();
		// Bounds stay in surface units (9..31 after the 1px slop); the area is those doubled into target pixels.
		walker.Bounds.ShouldBe((9f, 9f, 31f, 31f));
		walker.LayerArea.ShouldBe((18, 18, 44, 44));
	}
}
