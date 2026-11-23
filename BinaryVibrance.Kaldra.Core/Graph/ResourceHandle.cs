namespace BinaryVibrance.Kaldra.Core.Graph;

/// <summary>
/// An immutable handle to a declared resource — the currency the authoring API trades in. You get one from a
/// <see cref="Domain"/> verb (or <see cref="GraphBuilder.Persistent"/> / <see cref="GraphBuilder.Buffer{T}(string,int)"/> /
/// <see cref="GraphBuilder.Backbuffer"/>) and hand it to pass verbs (<c>.Writes</c>, <c>.Samples</c>, …).
/// <para>
/// This is an <b>abstract base</b>; every concrete handle is a <see cref="ColorHandle"/>, <see cref="DepthHandle"/>,
/// or <see cref="BufferHandle"/>. The subtypes let the pass verbs reject category errors at <em>compile time</em>
/// (you cannot <c>.WritesDepth</c> a colour target, <c>.Samples</c> a buffer, etc.) rather than at bake time
/// The marker interfaces <see cref="ITextureHandle"/> (sampleable) and
/// <see cref="IStorageHandle"/> (read-write storage) capture the capabilities verbs constrain against.
/// </para>
/// <para>
/// Identity is by reference: a handle <em>is</em> the resource, so ping-pong is just swapping two handles in a
/// normal loop. Structural hashing lives on <see cref="RenderGraph"/>, which walks handles by their stable
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

	/// <summary>
	/// The size of one element in bytes, for a buffer declared in terms of its element type or
	/// <see cref="Geometry.VertexLayout"/> (<see cref="GraphBuilder.Buffer{T}"/>,
	/// <see cref="GraphBuilder.Buffer(string,int,Geometry.VertexLayout)"/>, <see cref="GraphBuilder.Storage{T}"/>).
	/// <b>0 means unknown</b> — an image resource, or a buffer declared as an opaque byte size — so every consumer
	/// must treat 0 as "no contract to check against" rather than as a stride of zero.
	/// </summary>
	public int Stride { get; }

	/// <summary>
	/// How many elements the buffer was declared to hold — the counterpart of <see cref="Stride"/>, and 0 under
	/// exactly the same conditions. For a <see cref="Growable"/> storage buffer this is the <em>initial</em> element
	/// count, since a larger <c>IEngine.Write</c> reallocates.
	/// </summary>
	public int ElementCount { get; }

	/// <summary>
	/// For a <see cref="ResourceKind.Storage"/> buffer: whether its GPU allocation may grow at runtime. When true,
	/// <c>IEngine.Write</c> with a span larger than the current capacity reallocates the buffer (geometrically)
	/// instead of throwing; <see cref="ByteSize"/> is then the <em>initial</em> capacity, not a fixed one. A storage
	/// buffer's size is pure allocation — it doesn't feed the schedule, barriers, or aliasing — so growth is a local
	/// reallocation, never a graph rebuild. False (the default) for every other kind and for fixed storage buffers.
	/// </summary>
	public bool Growable { get; }

	/// <summary>True if this is the swapchain present target.</summary>
	public bool IsBackbuffer => Kind == ResourceKind.Backbuffer;

	/// <summary>
	/// Whether this resource's size tracks the swapchain, so a window resize has to reallocate it (and everything
	/// sized from it) at the new extent. True in the backbuffer domain and in a backbuffer-relative one; false for a
	/// fixed-size domain, which a resize leaves entirely alone — which is also why a <see cref="ResourceKind.Persistent"/>
	/// target on a fixed domain keeps its cross-frame contents across a resize while a screen-sized one cannot.
	/// <para>
	/// Stated once here rather than re-derived per backend: every place that has to decide what a resize rebuilds —
	/// transient images, persistent history, readback staging, on either backend — reads this, so the two backends
	/// cannot drift into resizing different sets of things.
	/// </para>
	/// </summary>
	public bool FollowsSwapchain => Domain.IsBackbuffer || Domain.RelativeScale > 0f;

	private protected ResourceHandle(int id, string name, Format format, ResourceKind kind, Domain domain, float scale, int count, int byteSize, bool growable = false, int stride = 0, int elementCount = 0)
	{
		Id = id;
		Name = name;
		Format = format;
		Kind = kind;
		Domain = domain;
		Scale = scale;
		Count = count;
		ByteSize = byteSize;
		Growable = growable;
		Stride = stride;
		ElementCount = elementCount;
	}

	/// <summary>The pixel dimensions of this resource, resolving <see cref="Scale"/> against its domain.</summary>
	public (int Width, int Height) Resolve() => Domain.Resolve(Scale);

	/// <summary>
	/// The pixel dimensions of this resource against a live swapchain extent. A backbuffer-domain resource follows
	/// the swapchain scaled by its own <see cref="Scale"/> — so a fractional mip sibling (e.g. a bloom pyramid level)
	/// shrinks while a full-resolution target stays native; a backbuffer-relative domain follows a fraction of the
	/// swapchain; a fixed-size domain uses its declared size. Clamped to at least 1×1. The parameterless
	/// <see cref="Resolve()"/> uses the domain's bake-time size; this overload re-resolves against the current
	/// swapchain, which is what a backend's transient-resource sizing needs after a resize.
	/// </summary>
	public (int Width, int Height) Resolve(int swapWidth, int swapHeight)
	{
		if (Domain.IsBackbuffer)
			return (ScaleDimension(swapWidth, Scale), ScaleDimension(swapHeight, Scale));
		if (Domain.RelativeScale > 0f)
			return (ScaleDimension(swapWidth, Domain.RelativeScale * Scale), ScaleDimension(swapHeight, Domain.RelativeScale * Scale));
		var (w, h) = Domain.Resolve(Scale);
		return (Math.Max(1, w), Math.Max(1, h));
	}

	private static int ScaleDimension(int value, float scale) => Math.Max(1, (int)MathF.Round(value * scale));

	public override string ToString() => $"{Name}:{Kind}<{Format}>";
}
