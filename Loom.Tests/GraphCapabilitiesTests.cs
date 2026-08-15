using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Graph;

namespace Loom.Tests;

/// <summary>
/// Checking a graph against what the device can do. The counterpart of the derive-time budget tests: those pin the
/// neutral floor every backend guarantees, these pin the ceiling a particular device imposes on top of it — and
/// that asking for more is one named failure rather than a validation-layer error or a silent downgrade.
/// </summary>
public class GraphCapabilitiesTests
{
	[Test]
	public void A_graph_declaring_a_sample_count_the_device_lacks_is_rejected()
	{
		var graph = RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180, samples: 8);
			g.Pass("p").Writes(world.Color("o", Format.Rgba8Srgb));
		});

		var ex = Should.Throw<GraphValidationException>(() => GraphCapabilities.Validate(graph, TestCapabilities.UpTo4xMsaa));

		ex.Message.ShouldContain("world");
		ex.Message.ShouldContain("8x MSAA");
		ex.ShouldBeAssignableTo<LoomAuthoringException>();
	}

	[Test]
	public void The_rejection_names_the_sample_counts_the_device_does_support()
	{
		// "8x is unsupported" leaves the author guessing what to write instead; the counts that would work are the
		// half of the message that gets them moving.
		var graph = RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180, samples: 8);
			g.Pass("p").Writes(world.Color("o", Format.Rgba8Srgb));
		});

		var ex = Should.Throw<GraphValidationException>(() => GraphCapabilities.Validate(graph, TestCapabilities.UpTo4xMsaa));

		ex.Message.ShouldContain("1, 2, 4");
	}

	[Test]
	public void A_sample_count_the_device_supports_passes()
	{
		var graph = RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180, samples: 4);
			g.Pass("p").Writes(world.Color("o", Format.Rgba8Srgb));
		});

		Should.NotThrow(() => GraphCapabilities.Validate(graph, TestCapabilities.UpTo4xMsaa));
	}

	[Test]
	public void A_single_sampled_graph_passes_on_the_narrowest_device()
	{
		// The overwhelmingly common case: nothing declared, nothing to check. It must not need a capable device.
		var graph = RenderGraph.Define(g =>
		{
			var world = g.Domain("world", 320, 180);
			g.Pass("p").Writes(world.Color("o", Format.Rgba8Srgb));
		});

		Should.NotThrow(() => GraphCapabilities.Validate(graph, TestCapabilities.Minimal));
	}

	[Test]
	public void Async_compute_is_rejected_when_the_engine_has_no_second_queue()
	{
		var graph = RenderGraph.Define(g =>
		{
			var buffer = g.Buffer("particles", 1024);
			g.Compute("sim").ReadWrites(buffer).Queue(Queue.AsyncCompute);
		});

		var ex = Should.Throw<GraphValidationException>(() => GraphCapabilities.Validate(graph, TestCapabilities.Minimal));

		ex.Message.ShouldContain("sim");
		ex.Message.ShouldContain("AsyncCompute");
	}

	[Test]
	public void Async_compute_is_accepted_by_an_engine_that_reports_a_second_queue()
	{
		// The check reads the capability rather than hard-coding "no backend has one", so the day a backend grows a
		// compute queue this test is what says the gate opened rather than needing to be deleted.
		var graph = RenderGraph.Define(g =>
		{
			var buffer = g.Buffer("particles", 1024);
			g.Compute("sim").ReadWrites(buffer).Queue(Queue.AsyncCompute);
		});

		Should.NotThrow(() => GraphCapabilities.Validate(graph, TestCapabilities.Permissive));
	}

	[Test]
	public void Every_capability_violation_in_a_graph_is_reported_together()
	{
		// Fixing a device mismatch one exception at a time is the slowest way to learn a device's limits.
		var graph = RenderGraph.Define(g =>
		{
			var wide = g.Domain("wide", 320, 180, samples: 8);
			var wider = g.Domain("wider", 320, 180, samples: 16);
			g.Pass("p").Writes(wide.Color("a", Format.Rgba8Srgb));
			g.Pass("q").Writes(wider.Color("b", Format.Rgba8Srgb));
			var buffer = g.Buffer("particles", 1024);
			g.Compute("sim").ReadWrites(buffer).Queue(Queue.AsyncCompute);
		});

		var ex = Should.Throw<GraphValidationException>(() => GraphCapabilities.Validate(graph, TestCapabilities.Minimal));

		ex.Message.ShouldContain("wide");
		ex.Message.ShouldContain("wider");
		ex.Message.ShouldContain("sim");
	}
}
