using System.Collections.Immutable;
using System.Text;

using BinaryVibrance.Kaldra.Core;
using BinaryVibrance.Kaldra.Core.Graph;

namespace BinaryVibrance.Kaldra.Graph;

/// <summary>
/// The transient-aliasing plan derived from a <see cref="LifetimePlan"/>: a partition of the aliasable
/// transients into <see cref="AliasBucket"/>s such that the resources in any one bucket have pairwise
/// non-overlapping lifetimes, so they can be backed by the <em>same</em> physical memory (e.g. the G-buffer's memory is reused by the bloom mips once the G-buffer is consumed). The number
/// of buckets equals the peak number of simultaneously-live transients — the minimum any aliasing scheme
/// can achieve.
/// <para>
/// Backend-neutral: this decides only <em>which</em> resources share memory (from lifetimes alone). The
/// backend turns each bucket into one allocation, binds the members to it, and emits the aliasing barrier
/// at each hand-off — and may split a bucket if the members' real memory requirements are incompatible (a
/// rarity for same-heap render targets). Non-aliasable resources (<see cref="ResourceKind.Persistent"/>,
/// the backbuffer) are excluded; the backend allocates those normally.
/// </para>
/// </summary>
public sealed class AliasPlan
{
	/// <summary>The source lifetimes.</summary>
	public LifetimePlan Lifetimes { get; }

	/// <summary>The buckets, in the order they were opened.</summary>
	public ImmutableArray<AliasBucket> Buckets { get; }

	/// <summary>Count of aliasable transients considered (the partition's input size).</summary>
	public int AliasableCount { get; }

	private readonly Dictionary<int, int> _bucketByResource; // ResourceHandle.Id -> bucket index

	private AliasPlan(LifetimePlan lifetimes, ImmutableArray<AliasBucket> buckets, int aliasableCount, Dictionary<int, int> bucketByResource)
	{
		Lifetimes = lifetimes;
		Buckets = buckets;
		AliasableCount = aliasableCount;
		_bucketByResource = bucketByResource;
	}

	/// <summary>The bucket a resource shares, or null if it is not aliasable (allocated on its own).</summary>
	public int? BucketOf(ResourceHandle resource)
	{
		ArgumentNullException.ThrowIfNull(resource);
		return _bucketByResource.TryGetValue(resource.Id, out var bucket) ? bucket : null;
	}

	/// <summary>
	/// The ids of bucket members that reuse memory an earlier member used this frame — every member after the first
	/// in each bucket. Their birth (Undefined→…) transition must wait on all prior work to that shared memory (the
	/// aliasing barrier); the first member of a bucket is the original user, covered by the per-frame fence instead.
	/// </summary>
	public IReadOnlySet<int> AliasedMemberIds()
	{
		var ids = new HashSet<int>();
		foreach (var bucket in Buckets)
			for (var i = 1; i < bucket.Members.Length; i++)
				ids.Add(bucket.Members[i].Id);
		return ids;
	}

	/// <summary>
	/// Derive the aliasing plan: first-fit interval partitioning. Aliasable lifetimes are taken in
	/// (start, end, id) order; each is placed in the first existing bucket whose last member ends strictly
	/// before this one starts (so they never coexist), else a new bucket is opened. Sorting by start makes
	/// first-fit optimal — it uses exactly the peak-overlap number of buckets — and deterministic.
	/// </summary>
	public static AliasPlan Derive(LifetimePlan lifetimes)
	{
		ArgumentNullException.ThrowIfNull(lifetimes);

		var ordered = lifetimes.Lifetimes
			.Where(l => l.Aliasable)
			.OrderBy(l => l.FirstUse)
			.ThenBy(l => l.LastUse)
			.ThenBy(l => l.Resource.Id)
			.ToList();

		var bucketEnd = new List<int>();                 // last assigned member's LastUse, per bucket
		var bucketMembers = new List<List<ResourceHandle>>();
		var byResource = new Dictionary<int, int>();

		foreach (var life in ordered)
		{
			var chosen = -1;
			for (var b = 0; b < bucketEnd.Count; b++)
			{
				if (bucketEnd[b] < life.FirstUse) // strictly before -> lifetimes don't share an order
				{
					chosen = b;
					break;
				}
			}

			if (chosen < 0)
			{
				chosen = bucketEnd.Count;
				bucketEnd.Add(life.LastUse);
				bucketMembers.Add([]);
			}
			else
			{
				bucketEnd[chosen] = life.LastUse;
			}

			bucketMembers[chosen].Add(life.Resource);
			byResource[life.Resource.Id] = chosen;
		}

		var buckets = ImmutableArray.CreateBuilder<AliasBucket>(bucketMembers.Count);
		for (var i = 0; i < bucketMembers.Count; i++)
			buckets.Add(new AliasBucket(i, [.. bucketMembers[i]]));

		return new AliasPlan(lifetimes, buckets.ToImmutable(), ordered.Count, byResource);
	}

	/// <summary>A human-readable dump of the buckets and the memory they save, for inspection and tests.</summary>
	public string Dump()
	{
		var sb = new StringBuilder();
		var saved = AliasableCount - Buckets.Length;
		sb.AppendLine($"AliasPlan ({Buckets.Length} buckets for {AliasableCount} transients, {saved} alias-folded)");
		foreach (var bucket in Buckets)
		{
			var members = string.Join(", ", bucket.Members.Select(m =>
			{
				var life = Lifetimes.Of(m)!.Value;
				return $"{m.Name}[{life.FirstUse}..{life.LastUse}]";
			}));
			sb.AppendLine($"  bucket {bucket.Index}: {members}");
		}
		return sb.ToString();
	}
}
