using System.Text.Json;

using Loom.Core;
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

	[Fact]
	public void ToDot_is_deterministic_across_identical_builds() =>
		Assert.Equal(Baseline().ToDot(), Baseline().ToDot());

	[Fact]
	public void ToDot_is_a_digraph_with_pass_and_resource_nodes_and_edges()
	{
		var dot = Baseline().ToDot();
		Assert.StartsWith("digraph RenderGraph {", dot);
		Assert.EndsWith("}\n", dot.ReplaceLineEndings("\n"));
		// Bipartite nodes.
		Assert.Contains("\"pass:lighting\"", dot);
		Assert.Contains("\"res:albedo\"", dot);
		// Read edge points resource -> pass; write edge points pass -> resource.
		Assert.Contains("\"res:albedo\" -> \"pass:lighting\"", dot);
		Assert.Contains("\"pass:lighting\" -> \"res:lit\"", dot);
		// Blend is a dashed, bidirectional read-modify-write edge.
		Assert.Contains("dir=both, style=dashed", dot);
	}

	[Fact]
	public void ToJson_is_deterministic_and_well_formed()
	{
		var json = Baseline().ToJson();
		Assert.Equal(json, Baseline().ToJson());

		using var doc = JsonDocument.Parse(json); // throws if malformed
		var root = doc.RootElement;
		Assert.Equal(Baseline().Hash(), root.GetProperty("Hash").GetString());
		Assert.Equal(2, root.GetProperty("Domains").GetArrayLength());          // world + backbuffer
		Assert.Equal(4, root.GetProperty("Passes").GetArrayLength());
		Assert.Equal("<backbuffer>", root.GetProperty("Present").GetString());

		// The lighting pass records its Sample(albedo) and Write(lit) edges.
		var lighting = root.GetProperty("Passes").EnumerateArray().Single(p => p.GetProperty("Name").GetString() == "lighting");
		var kinds = lighting.GetProperty("Accesses").EnumerateArray().Select(a => a.GetProperty("Kind").GetString()).ToArray();
		Assert.Contains("Sample", kinds);
		Assert.Contains("Write", kinds);
	}

	[Fact]
	public void Schedule_ToDot_is_deterministic_and_shows_the_derived_dag()
	{
		var schedule = GraphSchedule.Derive(Baseline());
		Assert.Equal(schedule.ToDot(), GraphSchedule.Derive(Baseline()).ToDot());

		var dot = schedule.ToDot();
		Assert.StartsWith("digraph GraphSchedule {", dot);
		// lighting depends on gbuffer (reads albedo it wrote) -> a dependency edge between the passes.
		Assert.Contains("\"gbuffer\" -> \"lighting\"", dot);
		// Execution order is rendered in the node labels.
		Assert.Contains("0: gbuffer", dot);
	}
}
