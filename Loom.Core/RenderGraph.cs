using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Loom.Core;

/// <summary>
/// An immutable, baked render-graph description: domains, resources, passes (with their access edges)
/// and the present target. This is the whole output of the Describe layer — it carries <em>intent</em>
/// only; no ordering, barriers, or GPU types (those are derived by the Execute layer in M3+).
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

	/// <summary>Passes, in program (declaration) order — which is itself a derivation input (Planning.md §6).</summary>
	public ImmutableArray<GraphPass> Passes { get; }

	/// <summary>The resource presented to the screen, or null if the graph declared no <c>Present</c>.</summary>
	public ResourceHandle? PresentTarget { get; }

	internal RenderGraph(
		ImmutableArray<Domain> domains,
		ImmutableArray<ResourceHandle> resources,
		ImmutableArray<GraphPass> passes,
		ResourceHandle? presentTarget)
	{
		Domains = domains;
		Resources = resources;
		Passes = passes;
		PresentTarget = presentTarget;
	}

	/// <summary>
	/// Build a render graph from a fluent authoring closure. This stands in for <c>engine.DefineGraph</c>
	/// (Planning.md §5) until the engine/Execute layer exists; the authoring surface is identical.
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
			sb.Append($"  {domain.Name}|{domain.Width}x{domain.Height}|bb={domain.IsBackbuffer}\n");

		sb.Append("resources\n");
		foreach (var resource in Resources)
			sb.Append($"  {resource.Id}|{resource.Name}|{resource.Kind}|{resource.Format}|@{resource.Domain.Name}|s={resource.Scale:R}|n={resource.Count}\n");

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
			var size = domain.IsBackbuffer ? "native" : $"{domain.Width}x{domain.Height}";
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
}
