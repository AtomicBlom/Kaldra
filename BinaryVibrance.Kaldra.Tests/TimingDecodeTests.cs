using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Graph;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// Turning raw GPU timestamp pairs into per-pass milliseconds. Backend-neutral on purpose (each backend collects the
/// ticks differently but the arithmetic must not differ), and the numbers it produces end up on a HUD, so a wrapped or
/// out-of-order pair has to read as zero rather than as a negative or absurd duration.
/// </summary>
public class TimingDecodeTests
{
	private static BarrierPlan TwoPasses() => BarrierPlan.Derive(GraphSchedule.Derive(RenderGraph.Define(g =>
	{
		var world = g.Domain("world", 320, 180);
		var lit = world.Color("lit", Format.Rgba16F);
		var screen = g.Backbuffer;
		g.Pass("scene").Writes(lit);
		g.Pass("blit").Samples(lit).Writes(screen);
		g.Present(screen);
	})));

	[Test]
	public void Each_pass_gets_its_own_begin_to_end_span_and_the_frame_total_spans_them_all()
	{
		var plan = TwoPasses();
		PassTiming[] timings = [];

		// Two passes: 10→20 and 30→60 ticks, at 0.5 ms/tick. The frame total is first begin → last end, so it
		// deliberately includes the 10-tick gap between the passes that summing the passes would miss.
		var total = TimingDecode.DecodePassTimings([10, 20, 30, 60], plan, 0.5, ref timings);

		timings.Length.ShouldBe(2);
		timings[0].Name.ShouldBe("scene");
		timings[0].GpuMilliseconds.ShouldBe(5.0);
		timings[1].Name.ShouldBe("blit");
		timings[1].GpuMilliseconds.ShouldBe(15.0);
		total.ShouldBe(25.0);
	}

	[Test]
	public void A_non_monotonic_pair_clamps_to_zero_rather_than_reporting_a_negative_duration()
	{
		var plan = TwoPasses();
		PassTiming[] timings = [];

		// The second pass's end precedes its begin — a driver wrap or oddity. Unsigned subtraction would wrap to a
		// astronomically large duration, so it has to clamp; the first pass is unaffected.
		var total = TimingDecode.DecodePassTimings([10, 20, 60, 30], plan, 0.5, ref timings);

		timings[0].GpuMilliseconds.ShouldBe(5.0);
		timings[1].GpuMilliseconds.ShouldBe(0.0);
		total.ShouldBe(10.0); // last end (30) - first begin (10)
	}

	[Test]
	public void A_frame_whose_last_end_precedes_its_first_begin_reports_zero_total()
	{
		var plan = TwoPasses();
		PassTiming[] timings = [];

		var total = TimingDecode.DecodePassTimings([100, 100, 100, 50], plan, 1.0, ref timings);

		total.ShouldBe(0.0);
	}

	[Test]
	public void The_timings_array_is_reallocated_when_the_pass_count_changes()
	{
		var plan = TwoPasses();
		PassTiming[] timings = new PassTiming[5]; // stale buffer from a previous, longer graph

		TimingDecode.DecodePassTimings([10, 20, 30, 60], plan, 1.0, ref timings);

		timings.Length.ShouldBe(2, "a graph swap changes the pass count, so the caller's buffer has to be resized or the HUD reads timings for passes that no longer exist.");
	}

	[Test]
	public void The_timings_array_is_reused_when_the_pass_count_is_unchanged()
	{
		var plan = TwoPasses();
		PassTiming[] timings = new PassTiming[2];
		var original = timings;

		TimingDecode.DecodePassTimings([10, 20, 30, 60], plan, 1.0, ref timings);

		// The steady state: same graph, same count, every frame. Reallocating here would allocate on the frame path.
		timings.ShouldBeSameAs(original);
	}

	[Test]
	public void No_timestamps_reports_zero_and_empties_the_timings()
	{
		var plan = TwoPasses();
		PassTiming[] timings = new PassTiming[2];

		var total = TimingDecode.DecodePassTimings([], plan, 1.0, ref timings);

		total.ShouldBe(0.0);
		timings.ShouldBeEmpty();
	}
}
