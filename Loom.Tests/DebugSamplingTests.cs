using Loom.Core.Graph;
using Loom.Graph;

namespace Loom.Tests;

/// <summary>
/// <see cref="RenderGraph.WithDebugSampling"/> — the resource viewer's hook, and the reason the viewer can preview an
/// intermediate target at all. Rather than reaching around the Execute layer to keep an image alive and in a
/// sampleable layout, it adds a real <see cref="AccessKind.Sample"/> edge from the terminal pass and lets the
/// derivation reach the same conclusion it would for any other read: "derived, not declared". These tests pin both
/// halves — the edge injection, and the keep-alive that falls out of it.
/// </summary>
public class DebugSamplingTests
{
	// A graph where `scratch` dies early: the middle pass is its last reader, so the aliaser is free to recycle its
	// memory for the rest of the frame — which is exactly what would leave the viewer previewing an aliased corpse.
	private static RenderGraph ShortLived() => RenderGraph.Define(g =>
	{
		var world = g.Domain("world", 320, 180);
		var screen = g.Backbuffer;
		var scratch = world.Color("scratch", Format.Rgba16F);
		var lit = world.Color("lit", Format.Rgba16F);
		g.Pass("produce").Writes(scratch);
		g.Pass("consume").Samples(scratch).Writes(lit);
		g.Pass("present").Samples(lit).Writes(screen);
		g.Present(screen);
	});

	private static int IdOf(RenderGraph graph, string name) =>
		graph.Resources.Single(r => r.Name == name).Id;

	[Test]
	public void A_sampled_edge_is_added_to_the_terminal_pass()
	{
		var graph = ShortLived();
		var scratch = IdOf(graph, "scratch");

		var executed = graph.WithDebugSampling([scratch]);

		var terminal = executed.Passes[^1];
		terminal.Name.ShouldBe("present");
		terminal.Accesses.ShouldContain(a => a.Resource.Id == scratch && a.Kind == AccessKind.Sample);
	}

	[Test]
	public void The_authors_graph_is_left_untouched()
	{
		var graph = ShortLived();
		var scratch = IdOf(graph, "scratch");

		var executed = graph.WithDebugSampling([scratch]);

		executed.ShouldNotBeSameAs(graph);
		graph.Passes[^1].Accesses.ShouldNotContain(a => a.Resource.Id == scratch);
	}

	[Test]
	public void The_injected_edge_keeps_the_resource_alive_to_the_last_pass()
	{
		// The point of making it a graph edge: the lifetime the aliaser reads is *derived*, not patched.
		var graph = ShortLived();
		var scratch = IdOf(graph, "scratch");

		var before = LifetimePlan.Derive(GraphSchedule.Derive(graph));
		var after = LifetimePlan.Derive(GraphSchedule.Derive(graph.WithDebugSampling([scratch])));

		var lastOrder = after.Schedule.Passes.Length - 1;
		before.Lifetimes.Single(l => l.Resource.Id == scratch).LastUse.ShouldBeLessThan(lastOrder);
		after.Lifetimes.Single(l => l.Resource.Id == scratch).LastUse.ShouldBe(lastOrder);
	}

	[Test]
	public void The_injected_edge_derives_a_shader_read_transition_on_the_terminal_pass()
	{
		// The other half of the keep-alive: alive is no use if it is still in COLOR_ATTACHMENT when the viewer binds it.
		var graph = ShortLived();
		var scratch = IdOf(graph, "scratch");

		var plan = BarrierPlan.Derive(GraphSchedule.Derive(graph.WithDebugSampling([scratch])));

		var terminal = plan.Passes[^1];
		// Already ShaderRead from the "consume" pass, so no transition is needed here — what matters is that the
		// resource is stored rather than discarded once it has a reader after its producer.
		plan.Passes.Single(p => p.Pass.Name == "produce").Attachments
			.Single(a => a.Resource.Id == scratch).Store.ShouldBe(StoreAction.Store);
		terminal.Pass.Accesses.ShouldContain(a => a.Resource.Id == scratch && a.Kind == AccessKind.Sample);
	}

	[Test]
	public void An_id_the_terminal_pass_already_accesses_is_not_duplicated()
	{
		var graph = ShortLived();
		var lit = IdOf(graph, "lit");

		var executed = graph.WithDebugSampling([lit]);

		// The terminal pass already samples `lit`; a second edge would bind the same texture twice and shift every
		// following slot index.
		executed.Passes[^1].Accesses.Count(a => a.Resource.Id == lit).ShouldBe(1);
	}

	[Test]
	public void Nothing_to_inject_returns_the_same_graph()
	{
		var graph = ShortLived();
		var lit = IdOf(graph, "lit");

		graph.WithDebugSampling([]).ShouldBeSameAs(graph);
		graph.WithDebugSampling([lit]).ShouldBeSameAs(graph); // the only requested id is already accessed
	}

	[Test]
	public void Out_of_range_ids_are_ignored()
	{
		var graph = ShortLived();

		graph.WithDebugSampling([-1, graph.Resources.Length, 9999]).ShouldBeSameAs(graph);
	}

	[Test]
	public void A_graph_with_no_passes_returns_itself()
	{
		var graph = RenderGraph.Define(g => g.Domain("world", 320, 180).Color("a", Format.Rgba8Srgb));

		graph.WithDebugSampling([0]).ShouldBeSameAs(graph);
	}

	[Test]
	public void Injection_does_not_change_the_terminal_passes_own_work()
	{
		var graph = ShortLived();
		var scratch = IdOf(graph, "scratch");
		var terminal = graph.Passes[^1];

		var executed = graph.WithDebugSampling([scratch]);

		var injected = executed.Passes[^1];
		injected.Name.ShouldBe(terminal.Name);
		injected.Kind.ShouldBe(terminal.Kind);
		injected.Queue.ShouldBe(terminal.Queue);
		injected.Record.ShouldBeSameAs(terminal.Record); // the pass still draws exactly what the author declared
	}
}
