using System.Collections.Immutable;
using Loom.Core.Hosting;

namespace Loom.Core.Graph;

/// <summary>
/// The fluent builder for a single pass. You state only <em>what</em> the pass reads and writes — every
/// verb records a <see cref="ResourceAccess"/> edge and returns <c>this</c> for chaining. Ordering,
/// barriers and layout transitions are <em>derived</em> from these edges by the Execute layer; any verb
/// that asked you to state order would be a design smell.
/// </summary>
public sealed class PassBuilder
{
	private readonly ImmutableArray<ResourceAccess>.Builder _accesses = ImmutableArray.CreateBuilder<ResourceAccess>();
	private readonly string _name;
	private readonly PassKind _kind;
	private Queue _queue = global::Loom.Core.Graph.Queue.Graphics;
	private Action<IRenderContext>? _record;

	internal PassBuilder(string name, PassKind kind)
	{
		_name = name;
		_kind = kind;
	}

	/// <summary>Colour output(s). The pass produces these; the engine may clear them.</summary>
	public PassBuilder Writes(params ReadOnlySpan<ColorHandle> resources)
	{
		foreach (var resource in resources)
			Add(AccessKind.Write, resource);
		return this;
	}

	/// <summary>
	/// Read-modify-write colour: the existing contents are loaded (preserved), then the technique's blend
	/// accumulates onto them. The pass is both the last reader and the next writer.
	/// </summary>
	public PassBuilder Blends(ColorHandle resource) => Add(AccessKind.Blend, resource);

	/// <summary>Sampled (shader-read) input(s) — any colour or depth image (<see cref="ITextureHandle"/>).</summary>
	public PassBuilder Samples(params ReadOnlySpan<ITextureHandle> resources)
	{
		foreach (var resource in resources)
			Add(AccessKind.Sample, (ResourceHandle)resource);
		return this;
	}

	/// <summary>Depth write.</summary>
	public PassBuilder WritesDepth(DepthHandle depth) => Add(AccessKind.WriteDepth, depth);

	/// <summary>Depth test, no write.</summary>
	public PassBuilder ReadsDepth(DepthHandle depth) => Add(AccessKind.ReadDepth, depth);

	/// <summary>General read-modify-write of read-write storage resources — storage images or buffers (<see cref="IStorageHandle"/>; used by compute).</summary>
	public PassBuilder ReadWrites(params ReadOnlySpan<IStorageHandle> resources)
	{
		foreach (var resource in resources)
			Add(AccessKind.ReadWrite, (ResourceHandle)resource);
		return this;
	}

	/// <summary>Read a buffer as vertex-attribute data (GPU particles — the draw reads what a compute wrote).</summary>
	public PassBuilder ReadsVertex(BufferHandle buffer) => Add(AccessKind.ReadVertex, buffer);

	/// <summary>
	/// Read an app-writable parameter buffer (<see cref="GraphBuilder.Constants{T}"/>) as the technique's
	/// uniform/constant input. The backend binds it to the technique's reflected uniform slot; the app fills it
	/// by handle via <c>IEngine.Write</c>. No barrier is derived (the buffer is host-coherent, CPU-written).
	/// </summary>
	public PassBuilder Reads(IUniformHandle constants) => Add(AccessKind.ReadUniform, (ResourceHandle)constants);

	/// <summary>
	/// Read an app-writable storage buffer (<see cref="GraphBuilder.Storage{T}"/>) as the technique's read-only
	/// structured-buffer input (an SSBO). The backend binds it to the technique's reflected storage-buffer slot;
	/// the app fills it by handle via <c>IEngine.Write</c> with a span. No barrier is derived (the buffer is
	/// host-coherent, CPU-written).
	/// </summary>
	public PassBuilder Reads(IStorageBufferHandle storage) => Add(AccessKind.ReadStorage, (ResourceHandle)storage);

	/// <summary>Assign this pass to a queue; the graph derives the cross-queue timeline sync.</summary>
	public PassBuilder Queue(Queue queue)
	{
		_queue = queue;
		return this;
	}

	/// <summary>
	/// The per-frame record callback for a raster pass. Must be pure w.r.t. graph structure: pull from
	/// <c>ctx</c> rather than capturing, so the graph stays frame-independent and cacheable.
	/// </summary>
	public PassBuilder Draw(Action<IRenderContext> record)
	{
		if (_kind != PassKind.Raster)
			throw new InvalidOperationException($"Pass '{_name}' is a compute pass; use .Dispatch, not .Draw.");
		_record = record;
		return this;
	}

	/// <summary>
	/// The per-frame record callback for a raster pass, given a <paramref name="state"/> captured once at build
	/// time. Write <paramref name="record"/> as a <c>static</c> lambda: it can then only reach <paramref name="state"/>
	/// (a stable object — an engine handle, or an app-side stable producer like a painter or a scene) and whatever
	/// it pulls from <c>ctx</c>, so a per-frame value can't accidentally be captured and frozen at build time. Read
	/// <paramref name="state"/>'s live members at execute time rather than snapshotting them into locals beforehand.
	/// </summary>
	public PassBuilder Draw<TState>(TState state, Action<IRenderContext, TState> record)
	{
		if (_kind != PassKind.Raster)
			throw new InvalidOperationException($"Pass '{_name}' is a compute pass; use .Dispatch, not .Draw.");
		_record = ctx => record(ctx, state);
		return this;
	}

	/// <summary>The per-frame record callback for a compute pass. Same purity discipline as <see cref="Draw(Action{IRenderContext})"/>.</summary>
	public PassBuilder Dispatch(Action<IRenderContext> record)
	{
		if (_kind != PassKind.Compute)
			throw new InvalidOperationException($"Pass '{_name}' is a raster pass; use .Draw, not .Dispatch.");
		_record = record;
		return this;
	}

	/// <summary>
	/// The per-frame record callback for a compute pass, given a <paramref name="state"/> captured once at build
	/// time. Same state-passing discipline as <see cref="Draw{TState}"/>: write <paramref name="record"/> as a
	/// <c>static</c> lambda so only <paramref name="state"/> and <c>ctx</c> are reachable, and read
	/// <paramref name="state"/>'s live members at execute time rather than capturing a per-frame value.
	/// </summary>
	public PassBuilder Dispatch<TState>(TState state, Action<IRenderContext, TState> record)
	{
		if (_kind != PassKind.Compute)
			throw new InvalidOperationException($"Pass '{_name}' is a raster pass; use .Draw, not .Dispatch.");
		_record = ctx => record(ctx, state);
		return this;
	}

	private PassBuilder Add(AccessKind kind, ResourceHandle resource)
	{
		_accesses.Add(new ResourceAccess(resource, kind));
		return this;
	}

	internal GraphPass Build() =>
		new(_name, _kind, _queue, _accesses.ToImmutable(), _record);
}
