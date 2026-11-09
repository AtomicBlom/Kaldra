using Loom.Core.Hosting;

namespace Loom.Graph;

/// <summary>
/// Mints the engine-unique ids that stable draw lists are kept under (<see cref="IEngine.AllocateDrawListStream"/>),
/// and answers whether an id was minted. A backend keeps one set of geometry buffers per stream, so the ids have to come
/// from one place: producers that each numbered their own streams from zero — as every UI host numbers its surfaces —
/// would land on the same buffers.
/// </summary>
public sealed class DrawListStreams
{
	// The newest id minted; minted ids run 1..this. Zero is never minted, so a hand-built identity left at its default
	// stream is refused like any other number the engine did not hand out.
	private int _minted;

	/// <summary>Mint the next stream id. Safe from any thread, since a producer may be built off the render thread.</summary>
	public int Allocate() => Interlocked.Increment(ref _minted);

	/// <summary>Whether <paramref name="stream"/> is an id <see cref="Allocate"/> has handed out.</summary>
	public bool IsMinted(int stream) => stream >= 1 && stream <= Volatile.Read(ref _minted);
}
