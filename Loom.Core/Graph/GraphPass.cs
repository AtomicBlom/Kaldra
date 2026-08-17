using System.Collections.Immutable;
using Loom.Core.Hosting;

namespace Loom.Core.Graph;

/// <summary>
/// An immutable, baked pass: its name, kind, queue, the resource-access edges it declared, and its
/// per-frame record callback. Produced by <see cref="PassBuilder"/> when the graph is built; never
/// mutated thereafter. The callback is excluded from the graph hash (it's frame data, not structure).
/// </summary>
public sealed class GraphPass
{
	/// <summary>The pass name (unique within the graph; used in dumps and the hash).</summary>
	public string Name { get; }

	/// <summary>Raster or compute.</summary>
	public PassKind Kind { get; }

	/// <summary>The queue this pass runs on.</summary>
	public Queue Queue { get; }

	/// <summary>Every resource this pass touches, in declaration order.</summary>
	public ImmutableArray<ResourceAccess> Accesses { get; }

	/// <summary>
	/// The per-frame record callback (<c>.Draw</c> for raster, <c>.Dispatch</c> for compute). May be
	/// null in graphs authored purely to inspect structure. Not part of the graph hash.
	/// </summary>
	public Action<IRenderContext>? Record { get; }

	internal GraphPass(string name, PassKind kind, Queue queue, ImmutableArray<ResourceAccess> accesses, Action<IRenderContext>? record)
	{
		Name = name;
		Kind = kind;
		Queue = queue;
		Accesses = accesses;
		Record = record;
	}

	/// <summary>The resources this pass touches with the given access kind.</summary>
	public IEnumerable<ResourceHandle> ResourcesWith(AccessKind kind)
	{
		foreach (var access in Accesses)
			if (access.Kind == kind)
				yield return access.Resource;
	}

	/// <summary>
	/// Fill <paramref name="ids"/> with the ids of the resources this pass touches under <paramref name="kind"/>, in
	/// declaration order, and return how many were written. The allocation-free counterpart to
	/// <see cref="ResourcesWith"/>, which is an iterator method — every backend calls this once per pass to resolve
	/// the constants and storage buffers a pass <c>.Reads</c> onto a technique's binding slots, and the steady render
	/// loop must not allocate.
	/// <para>
	/// Ids rather than handles because that is what the callers bind by, and it keeps the buffer a <c>stackalloc</c>
	/// span of blittable ints.
	/// </para>
	/// </summary>
	/// <remarks>
	/// Sized by the caller from the matching <see cref="GraphLimits"/> budget, which schedule derivation already
	/// rejects a pass for exceeding — so a derived plan cannot overflow <paramref name="ids"/>.
	/// </remarks>
	public int ResourceIdsWith(AccessKind kind, Span<int> ids)
	{
		var n = 0;
		foreach (var access in Accesses)
			if (access.Kind == kind)
				ids[n++] = access.Resource.Id;
		return n;
	}
}
