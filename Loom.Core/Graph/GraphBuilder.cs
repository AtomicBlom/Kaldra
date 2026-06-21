using System.Collections.Immutable;

namespace Loom.Core.Graph;

/// <summary>
/// The fluent entry point for authoring a render graph (the <c>g</c> in <c>DefineGraph(g => { … })</c>).
/// Declare resolution domains and resources, then passes that state their reads/writes. Build is
/// ordinary C#: feature toggles are control flow, not config (Planning.md §8). Call
/// <see cref="RenderGraph.Define"/> to drive one and get the immutable <see cref="RenderGraph"/> back.
/// <para>
/// <b>Backbuffer spelling (deviation from Planning.md §6 noted deliberately).</b> §5/§6 sketch the
/// swapchain as <c>g.Domain.Backbuffer</c> exposing a <c>.Color</c> property, while user domains use a
/// <c>.Color(name, format)</c> method — two spellings that collide in real C# (a type can't have both a
/// <c>Color</c> property and a <c>Color(…)</c> method, and <c>g.Domain</c> can't be both a property and a
/// method). Loom resolves this by exposing the swapchain colour target <em>directly</em> as the
/// <see cref="Backbuffer"/> handle. So §6's <c>var screen = g.Domain.Backbuffer; … .Writes(screen.Color)</c>
/// becomes <c>var screen = g.Backbuffer; … .Writes(screen)</c>; reach the native-resolution domain for
/// sibling targets via <c>g.Backbuffer.Domain</c>.
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

	private Domain? _backbufferDomain;
	private ColorHandle? _backbuffer;
	private ResourceHandle? _presentTarget;

	internal GraphBuilder() { }

	/// <summary>
	/// Declare a resolution domain. Everything declared in it renders at <paramref name="width"/>×
	/// <paramref name="height"/> — the mechanism behind pixelation (Planning.md §9).
	/// </summary>
	public Domain Domain(string name, int width, int height, int samples = 1)
	{
		if (width <= 0 || height <= 0)
			throw new ArgumentOutOfRangeException(nameof(width), $"Domain '{name}' must have positive dimensions.");
		ValidateSamples(name, samples);
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
		if (!_domainNames.Add(name))
			throw new InvalidOperationException($"Duplicate domain name '{name}'.");

		var domain = new Domain(this, name, width: 0, height: 0, isBackbuffer: false, relativeScale: scale, samples: samples);
		_domains.Add(domain);
		return domain;
	}

	// MSAA sample counts are powers of two (1, 2, 4, 8, 16). Validate at declaration so misuse fails loud (M7-A8).
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
	/// The swapchain colour target (native resolution; backend-chosen format). This <em>is</em> the
	/// present surface — hand it to <c>.Writes</c>/<c>.Blends</c> and to <see cref="Present"/>. Its
	/// <see cref="ResourceHandle.Domain"/> is the native-resolution domain, for declaring sibling
	/// native-res targets (e.g. a HUD at full resolution). See the type remarks for why this replaces
	/// §6's <c>screen.Color</c> spelling.
	/// </summary>
	public ColorHandle Backbuffer
	{
		get
		{
			if (_backbuffer is not null)
				return _backbuffer;

			_backbufferDomain = new Domain(this, "<backbuffer>", width: 0, height: 0, isBackbuffer: true);
			_domains.Add(_backbufferDomain);
			_backbuffer = (ColorHandle)AddResource("<backbuffer>", Format.Rgba8Srgb, ResourceKind.Backbuffer, _backbufferDomain, scale: 1f);
			return _backbuffer;
		}
	}

	/// <summary>
	/// Declare a resource whose lifetime is forever — the transient aliaser never recycles its memory. For
	/// cross-frame history that tolerates one-frame-stale self-feedback (e.g. auto-exposure), where a single
	/// pass reads and writes the same physical image.
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
		// Persistent resources aren't resolution-bound; anchor them to the backbuffer domain.
		return (ColorHandle)AddResource(name, format, ResourceKind.Persistent, BackbufferDomainForPersistent(), scale: 1f, count: size);
	}

	/// <summary>Begin a raster pass. Chain intents, then <c>.Draw(ctx => …)</c>.</summary>
	public PassBuilder Pass(string name) => AddPass(name, PassKind.Raster);

	/// <summary>Begin a compute pass. Chain intents, then <c>.Dispatch(ctx => …)</c>.</summary>
	public PassBuilder Compute(string name) => AddPass(name, PassKind.Compute);

	/// <summary>
	/// The terminal: declare which resource is presented to the screen. The acquire/present/frames-in-flight
	/// sync is derived behind this (Planning.md §6).
	/// </summary>
	public void Present(ColorHandle resource)
	{
		ArgumentNullException.ThrowIfNull(resource);
		_presentTarget = resource;
	}

	/// <summary>
	/// Declare that <paramref name="resource"/>'s contents should be read back to the CPU (Planning.md §7.4) —
	/// the GPU→CPU edge, used for picking. The backend copies it to host-visible memory after the frame; the
	/// value is available a frame or two later (readback is genuinely that old). Returns the handle so the
	/// host can query it. The keep-alive (the resource must survive to end-of-frame) is derived from this edge.
	/// </summary>
	public ResourceHandle Readback(ResourceHandle resource)
	{
		ArgumentNullException.ThrowIfNull(resource);
		if (!_readbackTargets.Contains(resource))
			_readbackTargets.Add(resource);
		return resource;
	}

	/// <summary>
	/// Declare a GPU storage buffer of <paramref name="byteSize"/> bytes (Planning.md §7.6) — not an image,
	/// not resolution-bound. A compute pass <c>.ReadWrites</c> it and a draw <c>.ReadsVertex</c> it (GPU
	/// particles): the engine derives the compute→vertex barrier from those edges. Anchored to the backbuffer
	/// domain (it has no resolution of its own).
	/// </summary>
	public BufferHandle Buffer(string name, int byteSize)
	{
		if (byteSize <= 0)
			throw new ArgumentOutOfRangeException(nameof(byteSize), "A buffer needs a positive byte size.");
		return (BufferHandle)AddResource(name, Format.Rgba8Unorm, ResourceKind.Buffer, BackbufferDomainForPersistent(), scale: 1f, byteSize: byteSize);
	}

	internal ResourceHandle AddResource(string name, Format format, ResourceKind kind, Domain domain, float scale, int count = 1, int byteSize = 0)
	{
		if (!_resourceNames.Add(name))
			throw new InvalidOperationException($"Duplicate resource name '{name}'.");
		var id = _resources.Count;
		ResourceHandle handle = kind switch
		{
			ResourceKind.Depth => new DepthHandle(id, name, format, domain, scale),
			ResourceKind.Buffer => new BufferHandle(id, name, format, domain, byteSize),
			// Color / Persistent / Backbuffer are all colour-like images; the lifetime/role lives in Kind.
			_ => new ColorHandle(id, name, format, kind, domain, scale, count),
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
		if (!_passNames.Add(name))
			throw new InvalidOperationException($"Duplicate pass name '{name}'.");
		var pass = new PassBuilder(name, kind);
		_passes.Add(pass);
		return pass;
	}

	internal RenderGraph Build()
	{
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
