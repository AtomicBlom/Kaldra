namespace Loom.Core;

/// <summary>
/// The logical queue a pass is assigned to. Backend-neutral, and <b>reserved for a future multi-queue
/// scheduler</b>: the enum and the <c>.Queue(...)</c> verb exist so the authoring surface is forward-compatible,
/// but the Execute layer builds a single linear timeline and both backends are single-queue, so
/// <see cref="AsyncCompute"/> is <b>not yet honoured</b>. Declaring it makes graph derivation fail loudly,
/// rather than silently running the pass on the graphics queue and giving a false impression of overlap.
/// </summary>
public enum Queue
{
	/// <summary>The main graphics queue. The default for every pass unless reassigned.</summary>
	Graphics,

	/// <summary>Intended for compute that overlaps graphics work. Not yet honoured (see the type remarks) —
	/// assigning it currently fails at derive time.</summary>
	AsyncCompute,
}
