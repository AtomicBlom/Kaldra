using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;

namespace BinaryVibrance.Kaldra.Graph;

/// <summary>
/// Checks a graph against what the device can actually do, so "this engine cannot run this graph" is one named
/// failure at <see cref="EngineBase.SetGraph"/> rather than a validation-layer error, a wrong picture, or a silent
/// downgrade. The counterpart to the derive-time budgets in <see cref="GraphLimits"/>: those are the neutral floor
/// every backend guarantees, this is the ceiling <em>this</em> device imposes on top of it.
/// </summary>
public static class GraphCapabilities
{
	/// <summary>
	/// Throw if <paramref name="graph"/> asks for something <paramref name="capabilities"/> says the device lacks.
	/// Every problem in the graph is reported together, because fixing them one exception at a time is the slowest
	/// way to learn a device's limits.
	/// </summary>
	/// <exception cref="GraphValidationException">The graph needs a capability the device does not have.</exception>
	public static void Validate(RenderGraph graph, EngineCapabilities capabilities)
	{
		ArgumentNullException.ThrowIfNull(graph);
		ArgumentNullException.ThrowIfNull(capabilities);

		// Allocated only once something is actually wrong: SetGraph is documented safe to call every frame, so the
		// clean path — which is every frame after the first — must not allocate at all.
		List<string>? errors = null;

		foreach (var domain in graph.Domains)
		{
			if (domain.Samples <= 1)
				continue;
			if ((capabilities.SampleCounts & (SampleCounts)domain.Samples) == 0)
				(errors ??= []).Add(
					$"Domain '{domain.Name}' requests {domain.Samples}x MSAA, but this device supports {Describe(capabilities.SampleCounts)}. "
					+ "Declare it at a supported sample count, or drop the samples argument.");
		}

		foreach (var pass in graph.Passes)
		{
			if (pass.Queue == Queue.AsyncCompute && !capabilities.AsyncCompute)
				(errors ??= []).Add(
					$"Pass '{pass.Name}' requests Queue.AsyncCompute, but this engine has no compute queue that overlaps graphics. "
					+ "Drop the .Queue(...) assignment until multi-queue lands.");
		}

		if (errors is not null)
			throw new GraphValidationException(string.Join(Environment.NewLine, errors));
	}

	// The supported counts as the numbers an author wrote, e.g. "1, 2, 4" — the flag values are the counts, so this
	// is a filter rather than a mapping table.
	private static string Describe(SampleCounts counts)
	{
		if (counts == SampleCounts.None)
			return "none";
		var supported = new List<string>(5);
		for (var count = 1; count <= 16; count <<= 1)
			if ((counts & (SampleCounts)count) != 0)
				supported.Add(count.ToString());
		return string.Join(", ", supported);
	}
}
