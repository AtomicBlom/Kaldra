using Loom.Core.Diagnostics;
using Loom.Core.Graph;
using Loom.Graph;

namespace Loom.Tests;

/// <summary>
/// The diagnostics seam. The engine's warnings used to go straight to stdout, which a windowed app has no way to
/// read — so what matters here is that an app can redirect them, and that the default still formats sensibly for the
/// console when nobody has.
/// </summary>
// LoomLog.Sink is process-wide, so these cannot share the process with tests that log: a concurrent engine warning
// would land in this class's capture, and this class's redirect would swallow that engine's diagnostics.
[NotInParallel]
public class LoomLogTests
{
	// Every test that sets the sink restores it, so a redirect here does not outlive the test that installed it.
	private static List<(LoomLogLevel Level, string Message)> Capture(Action body)
	{
		var captured = new List<(LoomLogLevel, string)>();
		var previous = LoomLog.Sink;
		try
		{
			LoomLog.Sink = (level, message) => captured.Add((level, message));
			body();
		}
		finally
		{
			LoomLog.Sink = previous;
		}
		return captured;
	}

	[Test]
	public void A_sink_receives_every_level_with_the_message_unformatted()
	{
		// The sink gets the raw message, not the console's "[loom] warning: " dressing — an app routing these into
		// its own logger supplies its own prefixes and severity mapping.
		var captured = Capture(() =>
		{
			LoomLog.Info("up on device X");
			LoomLog.Warning("pass 'ghost' may be dead");
			LoomLog.Error("device removed");
		});

		captured.Count.ShouldBe(3);
		captured[0].ShouldBe((LoomLogLevel.Info, "up on device X"));
		captured[1].ShouldBe((LoomLogLevel.Warning, "pass 'ghost' may be dead"));
		captured[2].ShouldBe((LoomLogLevel.Error, "device removed"));
	}

	[Test]
	public void Clearing_the_sink_restores_the_console_default()
	{
		Capture(() => LoomLog.Info("swallowed")).Count.ShouldBe(1);

		// Back to the default sink: nothing is captured, and writing must not throw.
		LoomLog.Sink.ShouldBeNull();
		Should.NotThrow(() => LoomLog.Info("goes to the console"));
	}

	[Test]
	public void The_dead_pass_warning_reaches_the_sink()
	{
		// The end-to-end reason the seam exists: a derive-time warning an app can actually surface.
		var captured = Capture(() =>
			GraphSchedule.Derive(RenderGraph.Define(g =>
			{
				var world = g.Domain("world", 320, 180);
				var used = world.Color("used", Format.Rgba8Srgb);
				var wasted = world.Color("wasted", Format.Rgba8Srgb);
				g.Pass("dead").Writes(wasted);
				g.Pass("real").Writes(used);
				g.Present(g.Backbuffer);
				g.Pass("blit").Samples(used).Writes(g.Backbuffer);
			})));

		captured.ShouldContain(m => m.Level == LoomLogLevel.Warning && m.Message.Contains("dead"));
	}
}
