namespace Loom.Core;

/// <summary>
/// An immutable handle to a declared resource — the currency the authoring API trades in. You get one from a
/// <see cref="Domain"/> verb (or <see cref="GraphBuilder.Persistent"/> / <see cref="GraphBuilder.Buffer"/> /
/// <see cref="GraphBuilder.Backbuffer"/>) and hand it to pass verbs (<c>.Writes</c>, <c>.Samples</c>, …).
/// <para>
/// This is an <b>abstract base</b>; every concrete handle is a <see cref="ColorHandle"/>, <see cref="DepthHandle"/>,
/// or <see cref="BufferHandle"/>. The subtypes let the pass verbs reject category errors at <em>compile time</em>
/// (you cannot <c>.WritesDepth</c> a colour target, <c>.Samples</c> a buffer, etc.) rather than at bake time
/// (M7 — Planning.md §8). The marker interfaces <see cref="ITextureHandle"/> (sampleable) and
/// <see cref="IStorageHandle"/> (read-write storage) capture the capabilities verbs constrain against.
/// </para>
/// <para>
/// Identity is by reference: a handle <em>is</em> the resource, so ping-pong is just swapping two handles in a
/// normal loop (§8). Structural hashing lives on <see cref="RenderGraph"/>, which walks handles by their stable
/// <see cref="Id"/> — never on the handle itself, whose <see cref="Domain"/> back-reference must not leak into a hash.
/// </para>
/// </summary>
public abstract class ResourceHandle
{
	/// <summary>Stable, graph-local index assigned at declaration. Drives deterministic dumps/hashing.</summary>
	public int Id { get; }

	/// <summary>The resource's name (unique within its graph; used in dumps and the hash).</summary>
	public string Name { get; }

	/// <summary>The abstract format. For <see cref="ResourceKind.Backbuffer"/> this is backend-resolved.</summary>
	public Format Format { get; }

	/// <summary>What the resource fundamentally is (its lifetime/role: colour, depth, buffer, persistent, backbuffer).</summary>
	public ResourceKind Kind { get; }

	/// <summary>The resolution domain this resource lives in.</summary>
	public Domain Domain { get; }

	/// <summary>Fraction of the domain resolution (1 = full). Only meaningful for colour mip targets.</summary>
	public float Scale { get; }

	/// <summary>For <see cref="ResourceKind.Persistent"/>: the history ring depth. Otherwise 1.</summary>
	public int Count { get; }

	/// <summary>For <see cref="ResourceKind.Buffer"/>: the buffer's size in bytes. 0 for image resources.</summary>
	public int ByteSize { get; }

	/// <summary>True if this is the swapchain present target.</summary>
	public bool IsBackbuffer => Kind == ResourceKind.Backbuffer;

	private protected ResourceHandle(int id, string name, Format format, ResourceKind kind, Domain domain, float scale, int count, int byteSize)
	{
		Id = id;
		Name = name;
		Format = format;
		Kind = kind;
		Domain = domain;
		Scale = scale;
		Count = count;
		ByteSize = byteSize;
	}

	/// <summary>The pixel dimensions of this resource, resolving <see cref="Scale"/> against its domain.</summary>
	public (int Width, int Height) Resolve() => Domain.Resolve(Scale);

	public override string ToString() => $"{Name}:{Kind}<{Format}>";
}

/// <summary>A handle that can be <c>.Samples</c>'d (read in a shader): a colour or depth image.</summary>
public interface ITextureHandle;

/// <summary>A handle that can be <c>.ReadWrites</c>'d (general read-modify-write storage): a storage image or a buffer.</summary>
public interface IStorageHandle;

/// <summary>
/// A colour image target. Produced by <c>.Writes</c>/<c>.Blends</c>, read by <c>.Samples</c>, and (as a
/// storage image — e.g. the persistent exposure value) by <c>.ReadWrites</c>. Also the static type of the
/// <see cref="GraphBuilder.Backbuffer"/> and <see cref="GraphBuilder.Persistent"/> image handles, which are
/// colour-like (their distinct lifetime is carried by <see cref="ResourceHandle.Kind"/>).
/// </summary>
public sealed class ColorHandle : ResourceHandle, ITextureHandle, IStorageHandle
{
	internal ColorHandle(int id, string name, Format format, ResourceKind kind, Domain domain, float scale, int count)
		: base(id, name, format, kind, domain, scale, count, byteSize: 0) { }
}

/// <summary>A depth/stencil target. Written by <c>.WritesDepth</c>, tested by <c>.ReadsDepth</c>, and sampleable.</summary>
public sealed class DepthHandle : ResourceHandle, ITextureHandle
{
	internal DepthHandle(int id, string name, Format format, Domain domain, float scale)
		: base(id, name, format, ResourceKind.Depth, domain, scale, count: 1, byteSize: 0) { }
}

/// <summary>
/// A GPU storage buffer (not an image). A compute pass <c>.ReadWrites</c> it and a draw <c>.ReadsVertex</c> it
/// (GPU particles — §7.6). Sized in bytes, not resolution-bound.
/// </summary>
public sealed class BufferHandle : ResourceHandle, IStorageHandle
{
	internal BufferHandle(int id, string name, Format format, Domain domain, int byteSize)
		: base(id, name, format, ResourceKind.Buffer, domain, scale: 1f, count: 1, byteSize: byteSize) { }
}
