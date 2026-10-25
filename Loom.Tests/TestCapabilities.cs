using Loom.Core.Hosting;

namespace Loom.Tests;

/// <summary>
/// Capability records for tests that need one but are not testing capabilities — a device-free
/// <see cref="IEngine"/> fake, or a graph check that should turn on one specific limit rather than on whatever a
/// real adapter happens to report.
/// </summary>
internal static class TestCapabilities
{
	/// <summary>Says yes to everything the record can express, so a test using it fails for its own reason rather
	/// than tripping a capability check it never meant to exercise.</summary>
	public static EngineCapabilities Permissive { get; } = new()
	{
		GpuTiming = true,
		AsyncCompute = true,
		SampleCounts = SampleCounts.X1 | SampleCounts.X2 | SampleCounts.X4 | SampleCounts.X8 | SampleCounts.X16,
		MaxColorAttachments = 8,
		MinUniformBufferOffsetAlignment = 256,
		Float32Filterable = true,
	};

	/// <summary>The narrowest device the record can describe: single-sampled, no async compute, no timing — what a
	/// capability check should reject a demanding graph against.</summary>
	public static EngineCapabilities Minimal { get; } = new()
	{
		GpuTiming = false,
		AsyncCompute = false,
		SampleCounts = SampleCounts.X1,
		MaxColorAttachments = 8,
		MinUniformBufferOffsetAlignment = 256,
		Float32Filterable = false,
	};

	/// <summary>A device offering 2x and 4x MSAA but nothing wider — for asserting that the rejection message names
	/// what <em>is</em> available rather than only what is not.</summary>
	public static EngineCapabilities UpTo4xMsaa { get; } = Minimal with
	{
		SampleCounts = SampleCounts.X1 | SampleCounts.X2 | SampleCounts.X4,
	};
}
