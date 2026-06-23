using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Loom.Core.Graph;

/// <summary>
/// An immutable, baked render-graph description: domains, resources, passes (with their access edges)
/// and the present target. This is the whole output of the Describe layer — it carries <em>intent</em>
/// only; no ordering, barriers, or GPU types (those are derived by the Execute layer).
/// <para>
/// It is hashable (<see cref="Hash"/>) so the bake cache can recognise an unchanged graph, and dumpable
/// (<see cref="Dump"/>) for inspection and tests. Authored via <see cref="Define"/>.
/// </para>
/// </summary>
public sealed class RenderGraph
{
	/// <summary>Resolution domains, in declaration order. The backbuffer domain (if used) is included.</summary>
	public ImmutableArray<Domain> Domains { get; }

	/// <summary>Declared resources, in declaration order (index == <see cref="ResourceHandle.Id"/>).</summary>
	public ImmutableArray<ResourceHandle> Resources { get; }

	/// <summary>Passes, in program (declaration) order — which is itself a derivation input.</summary>
	public ImmutableArray<GraphPass> Passes { get; }

	/// <summary>The resource presented to the screen, or null if the graph declared no <c>Present</c>.</summary>
	public ResourceHandle? PresentTarget { get; }

	/// <summary>
	/// Resources the CPU wants read back via <c>g.Readback</c> — the GPU→CPU edge. The backend
	/// copies each to host-visible memory after the frame; the result is available a frame or two later (the
	/// latency is real — readback is genuinely a frame or two old). Used by picking.
	/// </summary>
	public ImmutableArray<ResourceHandle> ReadbackTargets { get; }

	internal RenderGraph(
		ImmutableArray<Domain> domains,
		ImmutableArray<ResourceHandle> resources,
		ImmutableArray<GraphPass> passes,
		ResourceHandle? presentTarget,
		ImmutableArray<ResourceHandle> readbackTargets)
	{
		Domains = domains;
		Resources = resources;
		Passes = passes;
		PresentTarget = presentTarget;
		ReadbackTargets = readbackTargets;
	}

	/// <summary>
	/// Build a render graph from a fluent authoring closure. This stands in for <c>engine.DefineGraph</c>
	/// until the engine/Execute layer exists; the authoring surface is identical.
	/// </summary>
	public static RenderGraph Define(Action<GraphBuilder> author)
	{
		ArgumentNullException.ThrowIfNull(author);
		var builder = new GraphBuilder();
		author(builder);
		return builder.Build();
	}

	// Reading order for dumps: inputs first, then outputs.
	private static readonly AccessKind[] DisplayOrder =
	[
		AccessKind.Sample, AccessKind.ReadDepth, AccessKind.ReadWrite,
		AccessKind.Blend, AccessKind.Write, AccessKind.WriteDepth,
	];

	/// <summary>
	/// A stable, content-addressed hash of the graph's <em>structure</em> (domains, resources, passes and
	/// their access edges, the present target). Record callbacks are excluded — they are frame data, not
	/// structure. Accesses within a pass are canonicalised by sorting, so reordering sibling reads/writes
	/// doesn't bust the cache; pass order is preserved because program order is a real derivation input.
	/// </summary>
	public string Hash()
	{
		var sb = new StringBuilder();

		sb.Append("domains\n");
		foreach (var domain in Domains)
			sb.Append($"  {domain.Name}|{domain.Width}x{domain.Height}|bb={domain.IsBackbuffer}|rel={domain.RelativeScale:R}|s={domain.Samples}\n");

		sb.Append("resources\n");
		foreach (var resource in Resources)
			sb.Append($"  {resource.Id}|{resource.Name}|{resource.Kind}|{resource.Format}|@{resource.Domain.Name}|s={resource.Scale:R}|n={resource.Count}|b={resource.ByteSize}\n");

		sb.Append("passes\n");
		foreach (var pass in Passes)
		{
			sb.Append($"  {pass.Name}|{pass.Kind}|{pass.Queue}\n");
			foreach (var access in pass.Accesses.OrderBy(a => a.Kind).ThenBy(a => a.Resource.Id))
				sb.Append($"    {access.Kind}:{access.Resource.Id}\n");
		}

		sb.Append($"present {PresentTarget?.Id.ToString() ?? "none"}\n");

		var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
		return Convert.ToHexString(bytes);
	}

	/// <summary>A human-readable, deterministic dump of the graph for inspection and tests.</summary>
	public string Dump()
	{
		var sb = new StringBuilder();
		sb.AppendLine($"RenderGraph (hash {Hash()[..12]})");

		sb.AppendLine($"  Domains ({Domains.Length}):");
		foreach (var domain in Domains)
		{
			var size = domain.IsBackbuffer ? "native"
				: domain.RelativeScale > 0f ? $"{domain.RelativeScale:0.###}x screen"
				: $"{domain.Width}x{domain.Height}";
			if (domain.Samples > 1)
				size += $" {domain.Samples}xMSAA";
			sb.AppendLine($"    {domain.Name}  {size}");
		}

		sb.AppendLine($"  Resources ({Resources.Length}):");
		foreach (var resource in Resources)
		{
			var scale = resource.Scale == 1f ? "" : $"  scale={resource.Scale:0.###}";
			var ring = resource.Count == 1 ? "" : $"  ring={resource.Count}";
			sb.AppendLine($"    [{resource.Id}] {resource.Name}  {resource.Kind} {resource.Format}  @{resource.Domain.Name}{scale}{ring}");
		}

		sb.AppendLine($"  Passes ({Passes.Length}):");
		foreach (var pass in Passes)
		{
			sb.AppendLine($"    {pass.Name}  [{pass.Kind} on {pass.Queue}]");
			foreach (var kind in DisplayOrder)
			{
				var names = pass.ResourcesWith(kind).Select(r => r.Name).ToArray();
				if (names.Length > 0)
					sb.AppendLine($"        {kind,-10} -> {string.Join(", ", names)}");
			}
		}

		sb.AppendLine($"  Present: {PresentTarget?.Name ?? "(none)"}");
		return sb.ToString();
	}

	/// <summary>
	/// Return a graph identical to this one but with extra <see cref="AccessKind.Sample"/> edges from the terminal
	/// (last) pass to the given resources — the debug-viewer hook. Making the read a real graph edge means the
	/// Execute layer <em>derives</em> the keep-alive (the resource isn't recycled by transient aliasing before the
	/// terminal pass) and the shader-read transition, so the viewer samples a live, correctly-laid-out image instead
	/// of an aliased corpse — "derived, not declared". The author's graph is untouched; the engine
	/// applies this only to the graph it executes when the debug viewer is present. Ids the terminal pass already
	/// accesses are skipped; an empty set (or a graph with no passes) returns <c>this</c>.
	/// </summary>
	public RenderGraph WithDebugSampling(IReadOnlyCollection<int> resourceIds)
	{
		ArgumentNullException.ThrowIfNull(resourceIds);
		if (resourceIds.Count == 0 || Passes.Length == 0)
			return this;

		var lastIndex = Passes.Length - 1;
		var last = Passes[lastIndex];
		var existing = last.Accesses.Select(a => a.Resource.Id).ToHashSet();
		var additions = resourceIds.Where(id => id >= 0 && id < Resources.Length && existing.Add(id)).ToArray();
		if (additions.Length == 0)
			return this;

		var accesses = last.Accesses.ToBuilder();
		foreach (var id in additions)
			accesses.Add(new ResourceAccess(Resources[id], AccessKind.Sample));
		var newLast = new GraphPass(last.Name, last.Kind, last.Queue, accesses.ToImmutable(), last.Record);
		return new RenderGraph(Domains, Resources, Passes.SetItem(lastIndex, newLast), PresentTarget, ReadbackTargets);
	}

	// Reads (resource -> pass) vs writes (pass -> resource); Blend/ReadWrite are both (read-modify-write).
	private static bool IsRead(AccessKind kind) => kind is AccessKind.Sample or AccessKind.ReadDepth or AccessKind.ReadVertex;
	private static bool IsWrite(AccessKind kind) => kind is AccessKind.Write or AccessKind.WriteDepth;

	/// <summary>
	/// A GraphViz DOT rendering of the graph's data-flow: a bipartite digraph of passes (boxes,
	/// coloured by <see cref="PassKind"/>) and resources (ellipses for images, notes for buffers, coloured by
	/// <see cref="ResourceKind"/>). Read edges point resource→pass, write edges pass→resource, and read-modify-write
	/// (<c>.Blends</c>/<c>.ReadWrites</c>) edges are dashed + bidirectional. The present target is highlighted.
	/// Deterministic (declaration order) so it diffs cleanly. Render with <c>dot -Tpng</c>.
	/// </summary>
	public string ToDot()
	{
		var sb = new StringBuilder();
		sb.AppendLine("digraph RenderGraph {");
		sb.AppendLine("  rankdir=LR;");
		sb.AppendLine("  node [fontname=\"Segoe UI\"];");
		sb.AppendLine($"  labelloc=\"t\"; label=\"RenderGraph (hash {Hash()[..12]})\";");

		sb.AppendLine("  // resources");
		foreach (var r in Resources)
		{
			var shape = r.Kind == ResourceKind.Buffer ? "note" : "ellipse";
			var fill = r.Kind switch
			{
				ResourceKind.Color => "#cfe8ff",
				ResourceKind.Depth => "#ffe0b3",
				ResourceKind.Buffer => "#e0d4ff",
				ResourceKind.Persistent => "#d6f5d6",
				ResourceKind.Backbuffer => "#ffd6d6",
				_ => "#eeeeee",
			};
			var border = ReferenceEquals(r, PresentTarget) ? ", penwidth=3, color=\"#cc0000\"" : "";
			sb.AppendLine($"  \"res:{r.Name}\" [shape={shape}, style=filled, fillcolor=\"{fill}\", label=\"{r.Name}\\n{r.Kind} {r.Format}\"{border}];");
		}

		sb.AppendLine("  // passes");
		foreach (var p in Passes)
		{
			var fill = p.Kind == PassKind.Compute ? "#fff2b3" : "#d9d9d9";
			sb.AppendLine($"  \"pass:{p.Name}\" [shape=box, style=\"filled,rounded\", fillcolor=\"{fill}\", label=\"{p.Name}\\n[{p.Kind} on {p.Queue}]\"];");
		}

		sb.AppendLine("  // edges");
		foreach (var p in Passes)
		{
			foreach (var a in p.Accesses)
			{
				var pass = $"\"pass:{p.Name}\"";
				var res = $"\"res:{a.Resource.Name}\"";
				if (IsRead(a.Kind))
					sb.AppendLine($"  {res} -> {pass} [label=\"{a.Kind}\"];");
				else if (IsWrite(a.Kind))
					sb.AppendLine($"  {pass} -> {res} [label=\"{a.Kind}\"];");
				else // Blend / ReadWrite — read-modify-write
					sb.AppendLine($"  {pass} -> {res} [label=\"{a.Kind}\", dir=both, style=dashed];");
			}
		}

		sb.AppendLine("}");
		return sb.ToString();
	}

	/// <summary>
	/// A deterministic JSON rendering of the graph's structure: domains, resources, passes (with
	/// their access edges) and the present target — the same content as <see cref="Dump"/>, machine-readable for
	/// external tooling. Excludes record callbacks (frame data, not structure), matching <see cref="Hash"/>.
	/// </summary>
	public string ToJson()
	{
		var dto = new GraphDto(
			Hash(),
			[.. Domains.Select(d => new DomainDto(d.Name, d.Width, d.Height, d.IsBackbuffer, d.RelativeScale, d.Samples))],
			[.. Resources.Select(r => new ResourceDto(r.Id, r.Name, r.Kind.ToString(), r.Format.ToString(), r.Domain.Name, r.Scale, r.Count, r.ByteSize))],
			[.. Passes.Select(p => new PassDto(
				p.Name, p.Kind.ToString(), p.Queue.ToString(),
				[.. p.Accesses.Select(a => new AccessDto(a.Kind.ToString(), a.Resource.Id, a.Resource.Name))]))],
			PresentTarget?.Name);
		return JsonSerializer.Serialize(dto, JsonExportOptions);
	}

	private static readonly JsonSerializerOptions JsonExportOptions = new() { WriteIndented = true };

	private sealed record GraphDto(string Hash, DomainDto[] Domains, ResourceDto[] Resources, PassDto[] Passes, string? Present);
	private sealed record DomainDto(string Name, int Width, int Height, bool IsBackbuffer, float RelativeScale, int Samples);
	private sealed record ResourceDto(int Id, string Name, string Kind, string Format, string Domain, float Scale, int Count, int ByteSize);
	private sealed record PassDto(string Name, string Kind, string Queue, AccessDto[] Accesses);
	private sealed record AccessDto(string Kind, int ResourceId, string ResourceName);
}
