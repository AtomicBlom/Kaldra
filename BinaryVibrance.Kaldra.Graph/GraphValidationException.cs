using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;

namespace BinaryVibrance.Kaldra.Graph;

/// <summary>
/// Thrown when a <see cref="RenderGraph"/> cannot be scheduled into correct GPU work — e.g. a
/// transient resource is read before any pass writes it (it would sample garbage). Fail loud and early,
/// at derive time, rather than producing a graph that corrupts or stalls at runtime.
/// <para>
/// A <see cref="KaldraAuthoringException"/>: the graph describes work the app asked for, so this reports an authoring
/// mistake, not a device failure. Catching the base type catches derive-time and record-time mistakes together.
/// </para>
/// </summary>
public sealed class GraphValidationException(string message) : KaldraAuthoringException(message);
