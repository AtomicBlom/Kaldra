using System.Text.Json;
using Loom.Core.Graph;
using Loom.Graph;

namespace Loom.Tests;

/// <summary>M13 graph-viz export: <see cref="RenderGraph.ToDot"/>/<see cref="RenderGraph.ToJson"/> and
/// <see cref="GraphSchedule.ToDot"/> are deterministic, well-formed, and carry the graph's structure.</summary>
public class GraphExportTests
{
	// A small representative graph: a read edge, a write edge, a blend (read-modify-write), and a present.
	private static RenderGraph Baseline() => RenderGraph.Define(g =>
	{
		var world = g.Domain("world", 320, 180);
		var albedo = world.Color("albedo", Format.Rgba8Srgb);
		var lit = world.Color("lit", Format.Rgba16F);
		var screen = g.Backbuffer;
		g.Pass("gbuffer").Writes(albedo);
		g.Pass("lighting").Samples(albedo).Writes(lit);
		g.Pass("transparent").Blends(lit);
		g.Pass("upscale").Samples(lit).Writes(screen);
		g.Present(screen);
	});

	[Test]
	public void ToDot_is_deterministic_across_identical_builds() =>
		Baseline().ToDot().ShouldBe(Baseline().ToDot());

	[Test]
	public void ToDot_is_a_digraph_with_pass_and_resource_nodes_and_edges()
	{
		var dot = Baseline().ToDot();
		dot.ShouldStartWith("digraph RenderGraph {");
		dot.ReplaceLineEndings("\n").ShouldEndWith("}\n");
		// Bipartite nodes.
		dot.ShouldContain("\"pass:lighting\"");
		dot.ShouldContain("\"res:albedo\"");
		// Read edge points resource -> pass; write edge points pass -> resource.
		dot.ShouldContain("\"res:albedo\" -> \"pass:lighting\"");
		dot.ShouldContain("\"pass:lighting\" -> \"res:lit\"");
		// Blend is a dashed, bidirectional read-modify-write edge.
		dot.ShouldContain("dir=both, style=dashed");
	}

	[Test]
	public void ToJson_is_deterministic_and_well_formed()
	{
		var json = Baseline().ToJson();
		Baseline().ToJson().ShouldBe(json);

		using var doc = JsonDocument.Parse(json); // throws if malformed
		var root = doc.RootElement;
		root.GetProperty("Hash").GetString().ShouldBe(Baseline().Hash());
		root.GetProperty("Domains").GetArrayLength().ShouldBe(2);          // world + backbuffer
		root.GetProperty("Passes").GetArrayLength().ShouldBe(4);
		root.GetProperty("Present").GetString().ShouldBe("<backbuffer>");

		// The lighting pass records its Sample(albedo) and Write(lit) edges.
		var lighting = root.GetProperty("Passes").EnumerateArray().Single(p => p.GetProperty("Name").GetString() == "lighting");
		var kinds = lighting.GetProperty("Accesses").EnumerateArray().Select(a => a.GetProperty("Kind").GetString()).ToArray();
		kinds.ShouldContain("Sample");
		kinds.ShouldContain("Write");
	}

	[Test]
	public void Schedule_ToDot_is_deterministic_and_shows_the_derived_dag()
	{
		var schedule = GraphSchedule.Derive(Baseline());
		GraphSchedule.Derive(Baseline()).ToDot().ShouldBe(schedule.ToDot());

		var dot = schedule.ToDot();
		dot.ShouldStartWith("digraph GraphSchedule {");
		// lighting depends on gbuffer (reads albedo it wrote) -> a dependency edge between the passes.
		dot.ShouldContain("\"gbuffer\" -> \"lighting\"");
		// Execution order is rendered in the node labels.
		dot.ShouldContain("0: gbuffer");
	}
}
