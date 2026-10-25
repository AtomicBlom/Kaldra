using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;

namespace Loom.Graph;

/// <summary>
/// Checks each recorded draw against what its pass declared. Deriving the graph already rejects a declaration that
/// contradicts itself; this catches the <em>recording</em> contradicting the declaration — binding a resource the pass
/// never asked for, handing a mesh to a technique that fabricates its own vertices, pushing bytes no channel of the
/// technique can carry.
/// <para>
/// It is one implementation rather than three because it speaks only in neutral terms — a <see cref="GraphPass"/>, a
/// technique name, resource handles and byte counts — and never touches a backend type. That is also what makes the
/// bulk of it testable without a device.
/// </para>
/// <para>
/// <strong>Hot path.</strong> This runs per draw. Every scan is a manual <c>for</c> over
/// <see cref="GraphPass.Accesses"/>: no LINQ, no lambdas, and in particular never
/// <see cref="GraphPass.ResourcesWith"/>, which is an iterator method and allocates (its allocation-free counterpart
/// is <see cref="GraphPass.ResourceIdsWith"/>, which collects ids rather than searching for one). The steady render
/// loop must not allocate managed memory.
/// </para>
/// <para>
/// <strong>This is where a backend's record-path policy belongs.</strong> The verb implementations themselves
/// (<c>*RenderContext.Verbs.cs</c>) are deliberately left per backend and unshared: they are nearly all
/// descriptor-set, root-signature and bind-group construction, and the only neutral part is a small geometry-source
/// switch — an abstraction over that would carry more indirection than it removes. So when the three verb paths differ
/// on a <em>rule</em> rather than on an API call, the rule moves here instead of the code moving anywhere.
/// </para>
/// </summary>
public sealed class RecordValidator(bool enabled, TechniqueRegistry techniques, DrawListStreams streams)
{
	private GraphPass? _pass;

	/// <summary>
	/// Whether the per-draw declaration scans run. The checks that replace an existing throw — a null mesh, a push
	/// with nowhere to go — run either way, because gating those would <em>remove</em> a diagnostic in a release build
	/// rather than merely skip a new one.
	/// </summary>
	public bool Enabled { get; } = enabled;

	/// <summary>
	/// The bound used for a push into a constant buffer whose declared size reflection did not report — the portable
	/// device floor for a uniform buffer binding (Vulkan guarantees at least this much; DirectX 12 and WebGPU allow
	/// four times it). Deliberately a backstop against a runaway push, not a claim about the device: a real limit
	/// would be queried from the adapter.
	/// </summary>
	public const int UnknownUniformSlotBytes = 16 * 1024;

	/// <summary>The pass currently recording, for the messages. Null before the first pass of the frame.</summary>
	public GraphPass? Pass => _pass;

	private string PassName => _pass?.Name ?? "<none>";

	/// <summary>
	/// Note which pass is recording. Called from each backend's <c>BeginPass</c>; the validator holds the reference
	/// rather than a flattened copy, which costs nothing and keeps every access kind available.
	/// </summary>
	public void BeginPass(GraphPass pass) => _pass = pass;

	/// <summary>Validate a fullscreen pass's sampled inputs against the pass's <c>.Samples</c> declarations.</summary>
	public void FullScreen(string technique, ReadOnlySpan<ResourceHandle> inputs) =>
		RequireDeclared(technique, inputs, AccessKind.Sample, ".Samples");

	/// <summary>Validate a compute dispatch's sampled inputs and read-write storage against the pass's declarations.</summary>
	public void Dispatch(string technique, ReadOnlySpan<ResourceHandle> sampled, ReadOnlySpan<ResourceHandle> storage)
	{
		RequireDeclared(technique, sampled, AccessKind.Sample, ".Samples");
		RequireDeclared(technique, storage, AccessKind.ReadWrite, ".ReadWrites");
	}

	/// <summary>Validate a draw: its sampled inputs, its geometry source, and that it agrees with the technique's shape.</summary>
	public void Draw(in DrawCall call)
	{
		if (!Enabled)
			return;

		RequireDeclared(call.Technique, call.Sampled, AccessKind.Sample, ".Samples");
		if (call.SkinnedVertices is { } skinned)
			RequireDeclared(call.Technique, skinned, AccessKind.ReadVertex, ".ReadsVertex");
		if (call.Instances.GpuBuffer is { } instances)
			RequireDeclared(call.Technique, instances, AccessKind.ReadVertex, ".ReadsVertex");
		if (call.Instances.IndirectBuffer is { } arguments)
			RequireDeclared(call.Technique, arguments, AccessKind.ReadIndirect, ".ReadsIndirect");

		var record = techniques.Find(call.Technique);
		if (record is null)
			return; // an unregistered name is reported by the backend's own lookup, with a better message than this

		if (call.Mesh is not null && !record.TakesVertices)
		{
			throw new TechniqueMismatchException(
				$"Pass '{PassName}': the draw supplies a Mesh but technique '{call.Technique}' was defined without " +
				"HasVertexInput or a Layout, so it fabricates its own vertices from the vertex index and the mesh " +
				"would be ignored. Declare the layout, or drop the mesh and set VertexCount.",
				call.Technique);
		}

		if (call.Mesh is null && call.VertexCount <= 0 && call.Instances.IsEmpty)
		{
			throw new InvalidDrawCallException(
				PassName,
				call.Technique,
				$"the draw on technique '{call.Technique}' has no Mesh, so it is a procedural draw and needs " +
				$"VertexCount > 0 or an instance source; got VertexCount={call.VertexCount} and no instances.");
		}
	}

	/// <summary>
	/// The mesh a draw must have to reach a mesh-binding path. Always checked, even with validation off: the
	/// alternative is a bare "Nullable object must have a value" that names neither the pass nor the technique.
	/// </summary>
	public Mesh RequireMesh(in DrawCall call) =>
		call.Mesh ?? throw new InvalidDrawCallException(
			PassName,
			call.Technique,
			$"the draw on technique '{call.Technique}' binds mesh geometry but its Mesh is null. Set Mesh, or use a " +
			"procedural draw (VertexCount plus an instance source).");

	/// <summary>
	/// Validate a draw list's identity: a stable one must name a stream <see cref="IEngine.AllocateDrawListStream"/>
	/// minted. <strong>Always checked</strong> — it is one comparison, and what it prevents is silent: a backend keeps
	/// a stable stream's geometry and skips the upload while the revision holds, so two producers sharing a number at
	/// matching revisions would draw each other's geometry without any error.
	/// </summary>
	public void DrawList(string technique, in DrawListIdentity identity)
	{
		if (!identity.IsStable || streams.IsMinted(identity.Stream))
			return;
		throw new InvalidDrawCallException(
			PassName,
			technique,
			$"a draw list on technique '{technique}' names stable stream {identity.Stream}, which this engine's " +
			"AllocateDrawListStream did not mint. Allocate a stream per stable list from IEngine.AllocateDrawListStream, " +
			"or pass DrawListIdentity.Transient for a list that is rebuilt every frame.");
	}

	/// <summary>
	/// Validate that a push has somewhere to go and fits it. <strong>Always checked</strong>, because the alternative
	/// is what this replaces: the bytes silently truncated, or dropped entirely.
	/// <para>
	/// A push is delivered by exactly one channel. <em>Uniform</em>: when the technique reflects a constants buffer
	/// the pass's <c>.Reads</c> have not filled, the bytes are staged into that slot and must fit <em>its declared
	/// size</em> — it is a real buffer, but a buffer of the size the shader declared. <em>Scalar</em>: otherwise they
	/// go to the technique's inline push/root-constant block and must fit that. Neither available is an error.
	/// </para>
	/// <para>
	/// This is an overflow bound, not a layout check. A constant buffer's reflected size includes padding to its
	/// 16-byte rows, so a push that fits is not thereby laid out the way the shader reads it.
	/// </para>
	/// </summary>
	/// <param name="technique">The technique being recorded, for the message.</param>
	/// <param name="pushBytes">Size of the pushed span in bytes (<c>push.Length * sizeof(float)</c>).</param>
	/// <param name="routesToUniform">Whether the backend resolved this push to a free constant-buffer slot.</param>
	/// <param name="uniformSlot">Which slot it routes to; only read when <paramref name="routesToUniform"/>.</param>
	/// <param name="scalarPushBytes">Capacity of the technique's inline scalar block, or 0 when it declares none. Only read when the push does not route to a uniform slot.</param>
	public void PushFits(string technique, int pushBytes, bool routesToUniform, int uniformSlot, int scalarPushBytes)
	{
		if (pushBytes == 0)
			return;

		if (routesToUniform)
		{
			// A reflected 0 means the slot's size is unknown, not that it holds nothing — a shader with no sidecar
			// reports nothing at all. Falling back to the portable floor keeps the pathological case bounded without
			// rejecting a push the shader would have accepted.
			var declared = techniques.Find(technique)?.Shape.UniformSlotBytes(uniformSlot) ?? 0;
			var bound = declared > 0 ? declared : UnknownUniformSlotBytes;
			if (pushBytes > bound)
			{
				throw new TechniqueMismatchException(
					$"Pass '{PassName}': pushed {Floats(pushBytes)} to technique '{technique}', but the constant buffer " +
					$"at uniform slot {uniformSlot} holds {bound} bytes" +
					(declared > 0 ? "" : " (no reflected size; this is the portable fallback bound)") +
					". Shrink the push, or widen the block in the shader.",
					technique);
			}
			return;
		}

		if (scalarPushBytes == 0)
		{
			throw new TechniqueMismatchException(
				$"Pass '{PassName}': pushed {Floats(pushBytes)} to technique '{technique}', but it declares no " +
				"LOOM_PUSH_CONSTANT block and every constant buffer it reflects is already filled by the pass's " +
				".Reads. Declare a push block, or a spare ConstantBuffer for the push to land in.",
				technique);
		}

		if (pushBytes > scalarPushBytes)
		{
			throw new TechniqueMismatchException(
				$"Pass '{PassName}': pushed {Floats(pushBytes)} to technique '{technique}', but its scalar push block " +
				$"holds {scalarPushBytes} bytes. Move the block to a ConstantBuffer the pass .Reads, or shrink the push.",
				technique);
		}
	}

	// Both units, because the caller wrote a ReadOnlySpan<float> and the bound is a byte size.
	private static string Floats(int bytes) => $"{bytes / sizeof(float)} floats ({bytes} bytes)";

	// Every handle must carry a matching access edge on the recording pass. Compared by resource id, not by handle
	// identity: the resource-viewer rewrite (EngineBase.SetGraph) mints fresh passes that preserve ids and only ever
	// widen the sampled set, so it cannot make this fail spuriously.
	private void RequireDeclared(string technique, ReadOnlySpan<ResourceHandle> handles, AccessKind kind, string verb)
	{
		if (!Enabled || _pass is null || handles.IsEmpty)
			return;
		for (var i = 0; i < handles.Length; i++)
			RequireDeclared(technique, handles[i], kind, verb);
	}

	private void RequireDeclared(string technique, ResourceHandle handle, AccessKind kind, string verb)
	{
		if (!Enabled || _pass is null)
			return;

		var accesses = _pass.Accesses;
		for (var j = 0; j < accesses.Length; j++)
			if (accesses[j].Kind == kind && accesses[j].Resource.Id == handle.Id)
				return;

		throw new PassBindingException(
			_pass.Name,
			handle.Name,
			$"technique '{technique}' binds '{handle.Name}', but the pass never declared it — add " +
			$"{verb}({handle.Name}) to the pass. Ordering, barriers and image layouts are all derived from the " +
			"declared edges, so an undeclared binding is read in whatever layout the last pass left it in.");
	}
}
