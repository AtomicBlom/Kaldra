using Loom.Core.Graph;

namespace Loom.Graph;

/// <summary>
/// Thrown when a <see cref="RenderGraph"/> cannot be scheduled into correct GPU work — e.g. a
/// transient resource is read before any pass writes it (it would sample garbage). Fail loud and early,
/// at derive time, rather than producing a graph that corrupts or stalls at runtime.
/// </summary>
public sealed class GraphValidationException(string message) : Exception(message);
