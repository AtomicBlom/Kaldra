namespace Loom.Core;

/// <summary>
/// An immutable handle to a declared resource. This is the currency the authoring API trades in:
/// you get one from a <see cref="Domain"/> verb (or <see cref="GraphBuilder.Persistent"/> /
/// <see cref="GraphBuilder.Backbuffer"/>) and hand it to pass verbs (<c>.Writes</c>, <c>.Samples</c>, …).
/// <para>
/// Identity is by reference: a handle <em>is</em> the resource, so ping-pong is just swapping two
/// handles in a normal loop (Planning.md §8). Structural hashing lives on <see cref="RenderGraph"/>,
/// which walks handles by their stable <see cref="Id"/> — not on the handle itself, whose
/// <see cref="Domain"/> back-reference must not leak into a hash.
/// </para>
/// </summary>
public sealed class ResourceHandle
{
	/// <summary>Stable, graph-local index assigned at declaration. Drives deterministic dumps/hashing.</summary>
	public int Id { get; }

	/// <summary>The resource's name (unique within its graph; used in dumps and the hash).</summary>
	public string Name { get; }

	/// <summary>The abstract format. For <see cref="ResourceKind.Backbuffer"/> this is backend-resolved.</summary>
	public Format Format { get; }

	/// <summary>What the resource fundamentally is.</summary>
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

	internal ResourceHandle(int id, string name, Format format, ResourceKind kind, Domain domain, float scale, int count, int byteSize = 0)
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
