using Loom.Core.Hosting;

namespace Loom.Graph;

/// <summary>
/// Decodes GPU timestamp readbacks into per-pass timings. Collecting the raw timestamps is backend-specific (a
/// query pool vs a query heap, with different valid-bit masking), but turning begin/end tick pairs into
/// milliseconds and aligning them to the schedule's leaf passes is identical across backends, so it lives here.
/// </summary>
public static class TimingDecode
{
	/// <summary>
	/// Decode interleaved begin/end <paramref name="timestamps"/> (already masked to the queue's valid bits) into
	/// <paramref name="timings"/> — one <see cref="PassTiming"/> per leaf pass of <paramref name="plan"/>, resized
	/// as needed — and return the frame total in milliseconds (first begin → last end).
	/// <paramref name="millisecondsPerTick"/> converts a tick delta to milliseconds. A non-monotonic pair (a driver
	/// wrap or oddity) clamps to zero rather than producing a negative duration.
	/// </summary>
	public static double DecodePassTimings(ReadOnlySpan<ulong> timestamps, BarrierPlan plan, double millisecondsPerTick, ref PassTiming[] timings)
	{
		var passCount = timestamps.Length / 2;
		if (timings.Length != passCount)
			timings = new PassTiming[passCount];
		if (passCount == 0)
			return 0;

		for (var i = 0; i < passCount; i++)
		{
			var begin = timestamps[2 * i];
			var end = timestamps[2 * i + 1];
			var ticks = end >= begin ? end - begin : 0UL;
			timings[i] = new PassTiming(plan.Passes[i].Pass.Name, ticks * millisecondsPerTick);
		}

		var first = timestamps[0];
		var last = timestamps[^1];
		return (last >= first ? last - first : 0UL) * millisecondsPerTick;
	}
}
