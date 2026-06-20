namespace Loom.Core.Hosting;

/// <summary>
/// One leaf pass's GPU wall time for a completed frame (M13 profiling spine). Backend-neutral — no
/// graphics-API types; the backend fills these from timestamp queries read back after the frame's fence.
/// </summary>
/// <param name="Name">The pass's full (dotted) name, matching the graph's pass order.</param>
/// <param name="GpuMilliseconds">GPU time between the pass's begin and end timestamps, in milliseconds.</param>
public readonly record struct PassTiming(string Name, double GpuMilliseconds);
