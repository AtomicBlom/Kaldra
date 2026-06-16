using System.Collections.Immutable;

namespace Loom.Graph;

/// <summary>
/// Derives nested GPU debug-label scopes from dotted pass names — e.g. <c>bloom.down1</c> opens a
/// <c>bloom</c> group containing a <c>down1</c> leaf. Pure and backend-neutral: RenderDoc/PIX build
/// their event tree from begin/end <em>nesting depth</em>, not from the label text, so a flat label
/// per pass reads as siblings-with-dots. To get a real tree the engine opens a group scope whenever
/// the dotted prefix deepens and closes it when it ends.
/// <para>
/// Split rule: the last '.'-separated segment is the pass's own (leaf) label; everything before it is
/// the group path. No-dot names (<c>gbuffer</c>, <c>view</c>) stay top-level with no group wrapper.
/// </para>
/// </summary>
public static class DebugLabelScopes
{
	/// <summary>The label work for one pass, in emit order: close <see cref="Closes"/> open groups, open each of <see cref="Opens"/>, then open the pass's own <see cref="Leaf"/> scope (which the backend closes after recording the pass).</summary>
	public readonly record struct PassLabels(int Closes, ImmutableArray<string> Opens, string Leaf);

	/// <summary>The full derived sequence: per-pass label work (aligned with the input order) and the count of groups still open after the last pass (to close before the frame ends).</summary>
	public readonly record struct Plan(ImmutableArray<PassLabels> Passes, int TrailingCloses);

	/// <summary>Derive the label scopes for an ordered list of pass names (execution order).</summary>
	public static Plan Derive(IReadOnlyList<string> passNames)
	{
		ArgumentNullException.ThrowIfNull(passNames);

		var perPass = ImmutableArray.CreateBuilder<PassLabels>(passNames.Count);
		string[] open = []; // currently-open group path (segments; the leaf is never part of it)

		foreach (var name in passNames)
		{
			var segs = name.Split('.');
			var groupCount = segs.Length - 1; // the last segment is the leaf, the rest is the group path

			// Longest common prefix between the open path and this pass's group path: those stay open.
			var k = 0;
			while (k < open.Length && k < groupCount && open[k] == segs[k])
				k++;

			var opens = ImmutableArray.CreateBuilder<string>(groupCount - k);
			for (var i = k; i < groupCount; i++)
				opens.Add(segs[i]);

			perPass.Add(new PassLabels(open.Length - k, opens.ToImmutable(), segs[^1]));
			open = segs[..groupCount];
		}

		return new Plan(perPass.ToImmutable(), open.Length);
	}
}
