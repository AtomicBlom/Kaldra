using Loom.Core.Diagnostics;
using Loom.Core.Graph;
using Loom.Graph;

namespace Loom.Tests;

/// <summary>
/// The dead-pass warning scheduling emits — a diagnostic, not a rule, so these tests exist to say what it does and
/// does not claim. It reports a pass that produces something nothing downstream reads, presents, or reads back. What
/// it deliberately does not do is reason per-resource: a pass is exonerated as soon as <em>anything</em> it writes is
/// consumed, so a dead second output rides along unmentioned. That limitation is recorded here rather than left for
/// someone to rediscover as a bug — tightening it later should turn the last test's expectation around.
/// </summary>
[NotInParallel] // LoomLog.Sink is process-wide
public class DeadPassWarningTests
{
	private static List<string> WarningsFrom(Action<GraphBuilder> author)
	{
		var warnings = new List<string>();
		var previous = LoomLog.Sink;
		LoomLog.Sink = (level, message) =>
		{
			if (level == LoomLogLevel.Warning)
				warnings.Add(message);
		};
		try
		{
			GraphSchedule.Derive(RenderGraph.Define(author));
		}
		finally
		{
			LoomLog.Sink = previous;
		}
		return warnings;
	}

	[Test]
	public void A_pass_whose_output_nothing_consumes_is_reported()
	{
		var warnings = WarningsFrom(g =>
		{
			var world = g.Domain("world", 320, 180);
			var screen = g.Backbuffer;
			var orphan = world.Color("orphan", Format.Rgba16F);
			g.Pass("wasted").Writes(orphan); // written, never read, never presented, never read back
			g.Pass("view").Writes(screen);
			g.Present(screen);
		});

		warnings.ShouldContain(w => w.Contains("wasted") && w.Contains("dead"));
	}

	[Test]
	public void A_pass_that_feeds_the_present_target_is_not_reported()
	{
		var warnings = WarningsFrom(g =>
		{
			var screen = g.Backbuffer;
			g.Pass("view").Writes(screen);
			g.Present(screen);
		});

		warnings.ShouldBeEmpty();
	}

	[Test]
	public void A_pass_that_only_feeds_a_readback_target_is_not_reported()
	{
		// Picking: nothing samples the id buffer, and it is never presented — the g.Readback edge is the only thing
		// that makes the pass worth running, and the warning has to know that or it cries wolf on every picking graph.
		var warnings = WarningsFrom(g =>
		{
			var screen = g.Backbuffer;
			var ids = g.Screen.Color("ids", Format.R16F);
			g.Pass("picking").Writes(ids);
			g.Pass("view").Writes(screen);
			g.Readback(ids);
			g.Present(screen);
		});

		warnings.ShouldBeEmpty();
	}

	[Test]
	public void A_pass_whose_output_a_later_pass_consumes_is_not_reported()
	{
		var warnings = WarningsFrom(g =>
		{
			var world = g.Domain("world", 320, 180);
			var screen = g.Backbuffer;
			var lit = world.Color("lit", Format.Rgba16F);
			g.Pass("scene").Writes(lit);
			g.Pass("blit").Samples(lit).Writes(screen);
			g.Present(screen);
		});

		warnings.ShouldBeEmpty();
	}

	[Test]
	public void A_dead_second_output_of_an_otherwise_live_pass_is_currently_missed()
	{
		// Documents the known limit: `unused` is written and never read by anything, but the pass that writes it is a
		// prerequisite of `blit` (which consumes `lit`), so the check exonerates the whole pass and says nothing. The
		// warning is per-pass, not per-resource. If this ever starts warning, that is an improvement — update the test.
		var warnings = WarningsFrom(g =>
		{
			var world = g.Domain("world", 320, 180);
			var screen = g.Backbuffer;
			var lit = world.Color("lit", Format.Rgba16F);
			var unused = world.Color("unused", Format.Rgba16F);
			g.Pass("scene").Writes(lit, unused);
			g.Pass("blit").Samples(lit).Writes(screen);
			g.Present(screen);
		});

		warnings.ShouldBeEmpty();
	}
}
