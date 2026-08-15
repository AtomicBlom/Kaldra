namespace Loom.Core.Hosting;

/// <summary>
/// What the device behind a live <see cref="IEngine"/> can actually do — populated once at engine creation and
/// immutable for its lifetime. Every member is a fact the engine already probes or already throws for; this is the
/// fail-loud pillar made <em>queryable</em>, so a host can ask before it declares rather than discovering at
/// <see cref="IEngine.SetGraph"/>.
/// <para>
/// Members are <c>required init</c> rather than positional so that adding one is a compile error in every backend
/// that has not answered it — the same reason <see cref="EngineSettings.CopyBaseSettingsTo"/> exists. It also means
/// there is no <c>default</c> instance quietly claiming the device can do nothing.
/// </para>
/// <para>
/// Deliberately absent: a backend's own device-feature bits. Naming Vulkan's <c>dynamicRendering</c> or DirectX 12's
/// feature level here would put graphics-API vocabulary in the neutral layer, and a device lacking either is already
/// rejected during selection — so "this engine exists" implies them, and a field would only restate that.
/// </para>
/// </summary>
public sealed record EngineCapabilities
{
	/// <summary>
	/// Whether the queue can collect timestamps, and therefore whether <see cref="IEngine.GpuTimingEnabled"/> can be
	/// turned on at all. Setting that property to <c>true</c> on an engine that reports <c>false</c> here leaves it
	/// <c>false</c>.
	/// </summary>
	public required bool GpuTiming { get; init; }

	/// <summary>
	/// Whether compute can be scheduled on a queue that overlaps graphics. <c>false</c> on every backend today, which
	/// is what makes a graph declaring <c>Queue.AsyncCompute</c> fail at <see cref="IEngine.SetGraph"/> rather than
	/// silently running serialised.
	/// </summary>
	public required bool AsyncCompute { get; init; }

	/// <summary>
	/// The MSAA sample counts colour <em>and</em> depth targets both support — the intersection, because a graph's
	/// domain sizes both. Always includes <see cref="SampleCounts.X1"/>.
	/// </summary>
	public required SampleCounts SampleCounts { get; init; }

	/// <summary>
	/// The most colour attachments one pass may write on this device. This is the device's ceiling, which sits at or
	/// above the neutral floor every backend is held to (<see cref="Loom.Core.Graph.GraphLimits"/>) — a graph that
	/// stays under the floor is portable, and this reports the headroom above it.
	/// </summary>
	public required int MaxColorAttachments { get; init; }

	/// <summary>
	/// The required alignment, in bytes, of a dynamic uniform-buffer offset — the stride a per-draw uniform ring must
	/// round each allocation up to. Reported rather than validated: a backend already over-aligns to a value legal
	/// everywhere, and this is what would let it stop.
	/// </summary>
	public required int MinUniformBufferOffsetAlignment { get; init; }

	/// <summary>
	/// Whether 32-bit float formats can be sampled with a linear filter rather than only nearest. Reported rather
	/// than validated: a 32-bit float target is only a problem when a technique samples it <em>linearly</em>, and
	/// which filter a technique uses is not known when the graph is set — a rule there would reject working graphs.
	/// </summary>
	public required bool Float32Filterable { get; init; }
}
