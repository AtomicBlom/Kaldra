using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using BinaryVibrance.Kaldra.Core.Geometry;

namespace BinaryVibrance.Kaldra.Core.Graph;

/// <summary>
/// The fluent entry point for authoring a render graph (the <c>g</c> in <c>DefineGraph(g => { … })</c>).
/// Declare resolution domains and resources, then passes that state their reads/writes. Build is
/// ordinary C#: feature toggles are control flow, not config. Call
/// <see cref="RenderGraph.Define"/> to drive one and get the immutable <see cref="RenderGraph"/> back.
/// <para>
/// <b>Backbuffer spelling.</b> The swapchain colour target is exposed directly as the
/// <see cref="Backbuffer"/> handle: <c>var screen = g.Backbuffer; … .Writes(screen)</c>; reach the
/// native-resolution domain for sibling targets via <c>g.Backbuffer.Domain</c>.
/// </para>
/// </summary>
public sealed class GraphBuilder
{
	private readonly List<Domain> _domains = [];
	private readonly List<ResourceHandle> _resources = [];
	private readonly List<PassBuilder> _passes = [];
	private readonly HashSet<string> _domainNames = [];
	private readonly HashSet<string> _resourceNames = [];
	private readonly HashSet<string> _passNames = [];

	private readonly List<ResourceHandle> _readbackTargets = [];

	private readonly List<string> _scopes = [];
	private string? _scopePrefix;

	private Domain? _backbufferDomain;
	private ColorHandle? _backbuffer;
	private Vector4? _backbufferClearValue;
	private bool _backbufferNoClear;
	private ResourceHandle? _presentTarget;

	internal GraphBuilder() { }

	/// <summary>
	/// Declare a resolution domain. Everything declared in it renders at <paramref name="width"/>×
	/// <paramref name="height"/> — the mechanism behind pixelation.
	/// </summary>
	public Domain Domain(string name, int width, int height, int samples = 1)
	{
		if (width <= 0 || height <= 0)
			throw new ArgumentOutOfRangeException(nameof(width), $"Domain '{name}' must have positive dimensions.");
		ValidateSamples(name, samples);
		name = Qualify(name);
		if (!_domainNames.Add(name))
			throw new InvalidOperationException($"Duplicate domain name '{name}'.");

		var domain = new Domain(this, name, width, height, isBackbuffer: false, samples: samples);
		_domains.Add(domain);
		return domain;
	}

	/// <summary>
	/// Declare a <em>backbuffer-relative</em> resolution domain: its pixel size is <paramref name="scale"/> ×
	/// the native backbuffer, re-resolved at bake and on every resize so it tracks the window. Use this for
	/// full- or fractional-screen targets (e.g. <c>scale: 0.5f</c> for a half-res pass). For a pixel-art world
	/// that must stay a constant size regardless of window, use the fixed <see cref="Domain(string,int,int,int)"/>
	/// overload instead. (<see cref="Screen"/> is the <c>scale: 1</c> case — the native-resolution domain.)
	/// <paramref name="samples"/> is the MSAA count (1 = none; a power of two).
	/// </summary>
	public Domain Domain(string name, float scale, int samples = 1)
	{
		if (scale <= 0f)
			throw new ArgumentOutOfRangeException(nameof(scale), $"Domain '{name}' relative scale must be positive.");
		ValidateSamples(name, samples);
		name = Qualify(name);
		if (!_domainNames.Add(name))
			throw new InvalidOperationException($"Duplicate domain name '{name}'.");

		var domain = new Domain(this, name, width: 0, height: 0, isBackbuffer: false, relativeScale: scale, samples: samples);
		_domains.Add(domain);
		return domain;
	}

	// MSAA sample counts are powers of two (1, 2, 4, 8, 16). Validate at declaration so misuse fails loud.
	private static void ValidateSamples(string name, int samples)
	{
		if (samples < 1 || (samples & (samples - 1)) != 0)
			throw new ArgumentOutOfRangeException(nameof(samples), $"Domain '{name}' sample count must be a power of two (1, 2, 4, 8, …); got {samples}.");
	}

	/// <summary>
	/// The native-resolution screen domain — a convenience alias for <c>Backbuffer.Domain</c>. Declare
	/// full-resolution sibling targets here (e.g. a HUD scratch buffer at native res, on top of a pixelated
	/// world). Tracks the swapchain size on resize.
	/// </summary>
	public Domain Screen
	{
		get
		{
			_ = Backbuffer; // ensure the backbuffer domain exists
			return _backbufferDomain!;
		}
	}

	/// <summary>
	/// The colour the engine clears the backbuffer to before the first pass writes it. <c>null</c> (the
	/// default) clears to <see cref="Vector4.Zero"/> (transparent black). Must be set before the first
	/// <see cref="Backbuffer"/> access — the handle is immutable once created.
	/// </summary>
	public Vector4? BackbufferClear
	{
		get => _backbufferClearValue;
		set
		{
			if (_backbuffer is not null)
				throw new InvalidOperationException("Set BackbufferClear before the first g.Backbuffer access — the handle is immutable once created.");
			_backbufferClearValue = value;
		}
	}

	/// <summary>
	/// When <c>true</c>, the engine skips clearing the backbuffer on its first write (<c>LoadOp.DontCare</c>).
	/// Only correct when the first pass that writes the backbuffer is guaranteed to cover every pixel.
	/// Must be set before the first <see cref="Backbuffer"/> access.
	/// </summary>
	public bool BackbufferNoClear
	{
		get => _backbufferNoClear;
		set
		{
			if (_backbuffer is not null)
				throw new InvalidOperationException("Set BackbufferNoClear before the first g.Backbuffer access — the handle is immutable once created.");
			_backbufferNoClear = value;
		}
	}

	/// <summary>
	/// The swapchain colour target (native resolution; backend-chosen format). This <em>is</em> the
	/// present surface — hand it to <c>.Writes</c>/<c>.Blends</c> and to <see cref="Present"/>. Its
	/// <see cref="ResourceHandle.Domain"/> is the native-resolution domain, for declaring sibling
	/// native-res targets (e.g. a HUD at full resolution).
	/// </summary>
	public ColorHandle Backbuffer
	{
		get
		{
			if (_backbuffer is not null)
				return _backbuffer;

			_backbufferDomain = new Domain(this, "<backbuffer>", width: 0, height: 0, isBackbuffer: true);
			_domains.Add(_backbufferDomain);
			_backbuffer = (ColorHandle)AddResource("<backbuffer>", Format.Rgba8Srgb, ResourceKind.Backbuffer, _backbufferDomain, scale: 1f, clearValue: _backbufferClearValue, noClear: _backbufferNoClear);
			return _backbuffer;
		}
	}

	/// <summary>
	/// Declare a resource whose lifetime is forever — the transient aliaser never recycles its memory. For
	/// cross-frame history that tolerates one-frame-stale self-feedback (e.g. auto-exposure), where a single
	/// pass reads and writes the same physical image.
	/// <para>
	/// This form is anchored to the backbuffer domain, so it is <b>screen-sized</b> — the shape a full-resolution
	/// history buffer (TAA, temporal GI) wants. <b>Its contents do not survive a window resize:</b> the image is
	/// reallocated at the new extent and starts undefined, because there is no resample from the old size (both
	/// backends do this identically). A history buffer must therefore tolerate a reset, exactly as it tolerates the
	/// undefined first frame. For history that <em>should</em> outlive a resize, declare it on a fixed-size domain
	/// (<see cref="Domain.Persistent"/>) — a 1×1 exposure meter has no reason to be rebuilt and is left alone.
	/// </para>
	/// <para>
	/// <paramref name="size"/> is reserved as the history-ring depth for true read-previous / write-current
	/// temporal buffers (TAA, temporal GI). That ring is not implemented yet — both backends allocate a single
	/// physical image — so only <c>size == 1</c> is accepted; a larger value would silently alias to one image
	/// and hazard. Declaring <c>size &gt; 1</c> therefore fails here rather than misleading.
	/// </para>
	/// </summary>
	public ColorHandle Persistent(string name, Format format, int size = 1)
	{
		if (size < 1)
			throw new ArgumentOutOfRangeException(nameof(size), "Persistent ring depth must be at least 1.");
		if (size > 1)
		{
			throw new NotSupportedException(
				$"Persistent '{name}' requests a history-ring depth of {size}, but multi-slot history rings are not implemented yet (both backends allocate a single image). Use size == 1 (single-buffered self-feedback) until cross-frame ping-pong lands.");
		}
		// The backbuffer domain at scale 1 — a full-resolution history buffer that follows the swapchain.
		return (ColorHandle)AddResource(name, format, ResourceKind.Persistent, BackbufferDomainForPersistent(), scale: 1f, count: size);
	}

	/// <summary>
	/// Open a name scope: every domain, resource and pass declared until the returned handle is disposed gets
	/// <paramref name="name"/> prefixed to its own name, dot-separated. This is what lets a reusable graph fragment
	/// be instantiated more than once in one graph — two views, a portal, split-screen, a blur shared by the minimap
	/// and the main view — since names are otherwise unique per graph and the second instantiation collides.
	/// <code>
	/// using (g.Scope("left"))  left  = g.Bloom(litLeft);
	/// using (g.Scope("right")) right = g.Bloom(litRight);
	/// </code>
	/// The fragment itself needs no parameter and no change: it keeps declaring <c>bloom_mip0</c> and the scope
	/// makes that <c>left.bloom_mip0</c>. Scopes nest, and the qualified name is what the duplicate-name check and
	/// the graph hash see.
	/// <para>
	/// Scoping a pass name is free tooling: <c>DebugLabelScopes</c> derives the RenderDoc/PIX event tree from the
	/// same dots, so a scoped fragment's passes group under it automatically.
	/// </para>
	/// <para>
	/// <b>Techniques are not scoped.</b> A technique is registered on the engine (<c>IEngine.DefineTechnique</c>),
	/// not declared on the graph, so <c>ctx.FullScreen("bloom.prefilter", …)</c> inside a scope still names the
	/// technique exactly as registered. Only the three things a <see cref="GraphBuilder"/> mints are qualified.
	/// The backbuffer is excluded as well — there is one swapchain image whatever scope first touches it.
	/// </para>
	/// </summary>
	/// <returns>A scope handle to <c>using</c>. It is a <c>ref struct</c>, so it cannot escape the block that opened it.</returns>
	public NameScope Scope(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			throw new ArgumentException("A name scope needs a non-empty name.", nameof(name));
		// A scope may be dotted (Scope("view.left") == two nested scopes), but an empty segment would put a bare '.'
		// into every qualified name under it and open an unnamed group in the debug-label tree.
		if (name.StartsWith('.') || name.EndsWith('.') || name.Contains(".."))
			throw new ArgumentException($"Name scope '{name}' has an empty dot-separated segment.", nameof(name));

		_scopes.Add(name);
		_scopePrefix = string.Join('.', _scopes);
		return new NameScope(this, _scopes.Count);
	}

	// Close the innermost scope. `depth` is what the scope's own nesting level was when it opened, so closing out of
	// order (only reachable by calling Dispose by hand rather than via `using`) is caught instead of silently
	// unwinding somebody else's scope.
	private void PopScope(int depth)
	{
		if (_scopes.Count != depth)
		{
			throw new InvalidOperationException(
				$"Name scopes must be closed innermost-first; '{_scopes[depth - 1]}' was opened at depth {depth} but the graph is at depth {_scopes.Count}. Use a `using` statement.");
		}
		_scopes.RemoveAt(_scopes.Count - 1);
		_scopePrefix = _scopes.Count == 0 ? null : string.Join('.', _scopes);
	}

	// Prefix a graph-local name with the open scopes. Everything a GraphBuilder mints goes through here except the
	// backbuffer, which is one physical image regardless of who first asked for it.
	private string Qualify(string name) => _scopePrefix is null ? name : $"{_scopePrefix}.{name}";

	/// <summary>
	/// The handle returned by <see cref="Scope"/>: disposing it closes the scope. A <c>ref struct</c> so it cannot be
	/// stored in a field or captured, which keeps a scope's extent to the block that opened it.
	/// </summary>
	public readonly ref struct NameScope
	{
		private readonly GraphBuilder? _graph;
		private readonly int _depth;

		internal NameScope(GraphBuilder graph, int depth)
		{
			_graph = graph;
			_depth = depth;
		}

		/// <summary>Close the scope. A <c>default</c> scope was never opened, so disposing it does nothing.</summary>
		public void Dispose() => _graph?.PopScope(_depth);
	}

	/// <summary>Begin a raster pass. Chain intents, then <c>.Draw(ctx => …)</c>.</summary>
	public PassBuilder Pass(string name) => AddPass(name, PassKind.Raster);

	/// <summary>Begin a compute pass. Chain intents, then <c>.Dispatch(ctx => …)</c>.</summary>
	public PassBuilder Compute(string name) => AddPass(name, PassKind.Compute);

	/// <summary>
	/// The terminal: declare which resource is presented to the screen. The acquire/present/frames-in-flight
	/// sync is derived behind this.
	/// </summary>
	public void Present(ColorHandle resource)
	{
		ArgumentNullException.ThrowIfNull(resource);
		_presentTarget = resource;
	}

	/// <summary>
	/// Declare that <paramref name="resource"/>'s contents should be read back to the CPU —
	/// the GPU→CPU edge, used for picking. The backend copies it to host-visible memory after the frame; the
	/// value is available a frame or two later (readback is genuinely that old). Returns the handle so the
	/// host can query it. The keep-alive (the resource must survive to end-of-frame) is derived from this edge.
	/// </summary>
	public ResourceHandle Readback(ResourceHandle resource)
	{
		ArgumentNullException.ThrowIfNull(resource);
		// Depth is rejected here rather than left to a backend. The copy every backend records is colour-aspect only,
		// so a depth target would read back whatever that aspect resolves to — and until this said so, the accidental
		// gate was each backend's own texel-size table happening not to list Depth32F, which is not a rule anyone could
		// have found. Render the depth you need to sample into a colour target and read that back.
		if (resource.Kind == ResourceKind.Depth)
		{
			throw new ArgumentException(
				$"'{resource.Name}' is a depth target, and readback copies the colour aspect only. Write the values you " +
				"need into a colour target (an R32F or R16F pass output) and read that back instead.", nameof(resource));
		}
		if (!_readbackTargets.Contains(resource))
			_readbackTargets.Add(resource);
		return resource;
	}

	/// <summary>
	/// Declare a GPU storage buffer of <paramref name="byteSize"/> bytes — not an image,
	/// not resolution-bound. A compute pass <c>.ReadWrites</c> it and a draw <c>.ReadsVertex</c> it (GPU
	/// particles): the engine derives the compute→vertex barrier from those edges. Anchored to the backbuffer
	/// domain (it has no resolution of its own).
	/// <para>
	/// This overload declares a bag of bytes and nothing about what fills it. Prefer
	/// <see cref="Buffer{T}(string,int)"/> or <see cref="Buffer(string,int,VertexLayout)"/> whenever the buffer holds
	/// elements: the element stride reaches the handle instead of being multiplied away, so the draw path can check
	/// the technique's per-instance stream against it rather than corrupting memory silently. Reach for the byte
	/// overload only for a genuinely unstructured block.
	/// </para>
	/// </summary>
	public BufferHandle Buffer(string name, int byteSize)
	{
		if (byteSize <= 0)
			throw new ArgumentOutOfRangeException(nameof(byteSize), "A buffer needs a positive byte size.");
		return (BufferHandle)AddResource(name, Format.Rgba8Unorm, ResourceKind.Buffer, BackbufferDomainForPersistent(), scale: 1f, byteSize: byteSize);
	}

	/// <summary>
	/// Declare a GPU storage buffer holding <paramref name="count"/> elements of <typeparamref name="T"/> —
	/// <see cref="Buffer(string,int)"/> stated in terms of what the buffer holds, so the size arithmetic and the
	/// element stride are the engine's business rather than a magic number at the call site.
	/// <typeparamref name="T"/>'s layout must match the shader's <c>RWStructuredBuffer</c> element on every backend.
	/// </summary>
	public BufferHandle Buffer<T>(string name, int count) where T : unmanaged =>
		ElementBuffer(name, count, Unsafe.SizeOf<T>());

	/// <summary>
	/// Declare a GPU storage buffer holding <paramref name="count"/> elements laid out as
	/// <paramref name="layout"/> — the spelling for a buffer a compute pass fills and a draw then consumes as a
	/// per-instance vertex stream (the GPU-particle path). Naming the <em>same</em> <see cref="VertexLayout"/> the
	/// technique names as its <c>Instance</c> stream makes the buffer and the shader that walks it share one source
	/// of truth, and lets the draw path reject a mismatch instead of stepping through the buffer at the wrong stride.
	/// </summary>
	public BufferHandle Buffer(string name, int count, VertexLayout layout)
	{
		ArgumentNullException.ThrowIfNull(layout);
		return ElementBuffer(name, count, layout.Stride);
	}

	// The shared body of the two element-wise Buffer overloads: size the allocation from count x stride and keep both
	// on the handle. The multiply widens to long first so an implausibly large declaration fails loud instead of
	// wrapping to a negative (or small positive) byte size that would allocate a buffer nothing fits in.
	private BufferHandle ElementBuffer(string name, int count, int stride)
	{
		if (count <= 0)
			throw new ArgumentOutOfRangeException(nameof(count), $"Buffer '{name}' needs a positive element count.");
		var byteSize = (long)count * stride;
		if (byteSize > int.MaxValue)
			throw new ArgumentOutOfRangeException(nameof(count), $"Buffer '{name}' would need {byteSize} bytes ({count} elements x {stride} bytes), which overflows its 32-bit byte size.");
		return (BufferHandle)AddResource(name, Format.Rgba8Unorm, ResourceKind.Buffer, BackbufferDomainForPersistent(), scale: 1f, byteSize: (int)byteSize, stride: stride, elementCount: count);
	}

	/// <summary>
	/// Declare an app-writable parameter buffer of <c>sizeof(T)</c> bytes — a small uniform/constant block a
	/// technique reads. Fill it by handle each frame (or only on change) with <c>IEngine.Write</c>; a pass that
	/// <c>.Reads</c> it binds it to the technique's reflected uniform slot, so a technique's live parameters need
	/// no graph rebuild. <typeparamref name="T"/>'s field layout must match the shader's uniform block on both
	/// backends (std140 / cbuffer packing — prefer <c>float4</c> rows). Anchored to the backbuffer domain (it has
	/// no resolution of its own).
	/// </summary>
	public ConstantsHandle Constants<T>(string name) where T : unmanaged =>
		(ConstantsHandle)AddResource(name, Format.Rgba8Unorm, ResourceKind.Constants, BackbufferDomainForPersistent(), scale: 1f, byteSize: Unsafe.SizeOf<T>());

	/// <summary>
	/// Declare an app-writable storage buffer of <paramref name="count"/> × <c>sizeof(T)</c> bytes — a read-only
	/// structured array (SSBO) a technique reads, for data too large or too dynamically-indexed for a uniform block.
	/// Fill it by handle each frame (or only on change) with <c>IEngine.Write</c> passing a span; a pass that
	/// <c>.Reads</c> it binds it to the technique's reflected storage-buffer slot, so its contents need no graph
	/// rebuild. <typeparamref name="T"/>'s layout must match the shader's structured-buffer element on both backends.
	/// Anchored to the backbuffer domain (it has no resolution of its own). When <paramref name="growable"/> the
	/// <paramref name="count"/> is an initial capacity and a larger <c>IEngine.Write</c> grows the GPU allocation
	/// (geometrically) instead of throwing — the substrate for a dynamically-sized buffer.
	/// </summary>
	public StorageBufferHandle Storage<T>(string name, int count, bool growable = false) where T : unmanaged
	{
		if (count <= 0)
			throw new ArgumentOutOfRangeException(nameof(count), "A storage buffer needs a positive element count.");
		var stride = Unsafe.SizeOf<T>();
		return (StorageBufferHandle)AddResource(name, Format.Rgba8Unorm, ResourceKind.Storage, BackbufferDomainForPersistent(), scale: 1f, byteSize: count * stride, growable: growable, stride: stride, elementCount: count);
	}

	internal ResourceHandle AddResource(string name, Format format, ResourceKind kind, Domain domain, float scale, int count = 1, int byteSize = 0, bool growable = false, Vector4? clearValue = null, bool noClear = false, int stride = 0, int elementCount = 0, float clearDepth = 1f)
	{
		// Both APIs define the depth clear over the [0,1] depth range; anything else is undefined rather than a
		// deeper far plane. Catch the reversed-Z typo (clearDepth: -1) at declaration instead of at bake.
		if (kind == ResourceKind.Depth && clearDepth is < 0f or > 1f)
			throw new ArgumentOutOfRangeException(nameof(clearDepth), $"Depth target '{name}' clears to {clearDepth}; the depth range is [0, 1] (1 = conventional far plane, 0 = reversed-Z far plane).");

		// The backbuffer is the one resource a scope must not rename: it is created lazily on first access, so which
		// scope happens to touch it first is an accident of authoring order, not a naming decision.
		if (kind != ResourceKind.Backbuffer)
			name = Qualify(name);
		if (!_resourceNames.Add(name))
			throw new InvalidOperationException($"Duplicate resource name '{name}'.");
		var id = _resources.Count;
		ResourceHandle handle = kind switch
		{
			ResourceKind.Depth => new DepthHandle(id, name, format, domain, scale, clearDepth, noClear),
			ResourceKind.Buffer => new BufferHandle(id, name, format, domain, byteSize, stride, elementCount),
			ResourceKind.Constants => new ConstantsHandle(id, name, domain, byteSize),
			ResourceKind.Storage => new StorageBufferHandle(id, name, domain, byteSize, growable, stride, elementCount),
			// Color / Persistent / Backbuffer are all colour-like images; the lifetime/role lives in Kind.
			_ => new ColorHandle(id, name, format, kind, domain, scale, count, clearValue, noClear)
		};
		_resources.Add(handle);
		return handle;
	}

	private Domain BackbufferDomainForPersistent()
	{
		_ = Backbuffer; // ensure the backbuffer domain exists
		return _backbufferDomain!;
	}

	private PassBuilder AddPass(string name, PassKind kind)
	{
		name = Qualify(name);
		if (!_passNames.Add(name))
			throw new InvalidOperationException($"Duplicate pass name '{name}'.");
		var pass = new PassBuilder(name, kind);
		_passes.Add(pass);
		return pass;
	}

	internal RenderGraph Build()
	{
		// An unclosed scope means the authoring closure returned mid-scope — every declaration after the missing
		// Dispose silently kept the prefix. Say so here rather than leaving the author to wonder why half the graph
		// is named oddly.
		if (_scopes.Count > 0)
		{
			throw new InvalidOperationException(
				$"Name scope '{_scopePrefix}' was never closed. Wrap g.Scope(...) in a `using` statement so it closes even when the block exits early.");
		}

		var passes = ImmutableArray.CreateBuilder<GraphPass>(_passes.Count);
		foreach (var pass in _passes)
			passes.Add(pass.Build());

		return new RenderGraph(
			[.. _domains],
			[.. _resources],
			passes.ToImmutable(),
			_presentTarget,
			[.. _readbackTargets]);
	}
}
