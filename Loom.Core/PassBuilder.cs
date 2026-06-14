using System.Collections.Immutable;

namespace Loom.Core;

/// <summary>
/// The fluent builder for a single pass. You state only <em>what</em> the pass reads and writes — every
/// verb records a <see cref="ResourceAccess"/> edge and returns <c>this</c> for chaining. Ordering,
/// barriers and layout transitions are <em>derived</em> from these edges by the Execute layer; any verb
/// that asked you to state order would be a design smell (Planning.md §8).
/// </summary>
public sealed class PassBuilder
{
	private readonly ImmutableArray<ResourceAccess>.Builder _accesses = ImmutableArray.CreateBuilder<ResourceAccess>();
	private readonly string _name;
	private readonly PassKind _kind;
	private Queue _queue = global::Loom.Core.Queue.Graphics;
	private Action<IRenderContext>? _record;

	internal PassBuilder(string name, PassKind kind)
	{
		_name = name;
		_kind = kind;
	}

	/// <summary>Colour output(s). The pass produces these; the engine may clear them.</summary>
	public PassBuilder Writes(params ReadOnlySpan<ResourceHandle> resources) => Add(AccessKind.Write, resources);

	/// <summary>
	/// Read-modify-write colour: <c>LoadOp.Load</c> + the technique's blend accumulates onto the existing
	/// contents. The pass is both the last reader and the next writer (Planning.md §8).
	/// </summary>
	public PassBuilder Blends(ResourceHandle resource) => Add(AccessKind.Blend, resource);

	/// <summary>Sampled (shader-read) input(s).</summary>
	public PassBuilder Samples(params ReadOnlySpan<ResourceHandle> resources) => Add(AccessKind.Sample, resources);

	/// <summary>Depth write.</summary>
	public PassBuilder WritesDepth(ResourceHandle depth) => Add(AccessKind.WriteDepth, depth);

	/// <summary>Depth test, no write.</summary>
	public PassBuilder ReadsDepth(ResourceHandle depth) => Add(AccessKind.ReadDepth, depth);

	/// <summary>General read-modify-write (storage images/buffers; used by compute).</summary>
	public PassBuilder ReadWrites(params ReadOnlySpan<ResourceHandle> resources) => Add(AccessKind.ReadWrite, resources);

	/// <summary>Read a buffer as vertex-attribute data (GPU particles — the draw reads what a compute wrote).</summary>
	public PassBuilder ReadsVertex(ResourceHandle buffer) => Add(AccessKind.ReadVertex, buffer);

	/// <summary>Assign this pass to a queue; the graph derives the cross-queue timeline sync.</summary>
	public PassBuilder Queue(Queue queue)
	{
		_queue = queue;
		return this;
	}

	/// <summary>
	/// The per-frame record callback for a raster pass. Must be pure w.r.t. graph structure: pull from
	/// <c>ctx</c> rather than capturing, so the graph stays frame-independent and cacheable (§8).
	/// </summary>
	public PassBuilder Draw(Action<IRenderContext> record)
	{
		if (_kind != PassKind.Raster)
			throw new InvalidOperationException($"Pass '{_name}' is a compute pass; use .Dispatch, not .Draw.");
		_record = record;
		return this;
	}

	/// <summary>The per-frame record callback for a compute pass. Same purity discipline as <see cref="Draw"/>.</summary>
	public PassBuilder Dispatch(Action<IRenderContext> record)
	{
		if (_kind != PassKind.Compute)
			throw new InvalidOperationException($"Pass '{_name}' is a raster pass; use .Draw, not .Dispatch.");
		_record = record;
		return this;
	}

	private PassBuilder Add(AccessKind kind, ResourceHandle resource)
	{
		_accesses.Add(new ResourceAccess(resource, kind));
		return this;
	}

	private PassBuilder Add(AccessKind kind, ReadOnlySpan<ResourceHandle> resources)
	{
		foreach (var resource in resources)
			_accesses.Add(new ResourceAccess(resource, kind));
		return this;
	}

	internal GraphPass Build() =>
		new(_name, _kind, _queue, _accesses.ToImmutable(), _record);
}
