namespace Loom.Core;

/// <summary>
/// The logical queue a pass is assigned to. The author states intent (<c>.Queue(Queue.AsyncCompute)</c>);
/// the graph derives the cross-queue timeline synchronisation. Backend-neutral.
/// </summary>
public enum Queue
{
	/// <summary>The main graphics queue. The default for every pass unless reassigned.</summary>
	Graphics,

	/// <summary>A queue for compute that overlaps graphics work.</summary>
	AsyncCompute,
}
