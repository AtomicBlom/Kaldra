namespace Loom.Core.Hosting;

/// <summary>
/// Identity of a draw-list geometry stream re-submitted across frames — the neutral handle a backend keys kept
/// vertex and index buffers by, so an unchanged list is not re-uploaded every frame.
/// <para>
/// Deliberately not named for the UI. A draw list is the engine's batched-geometry verb and a UI surface is only its
/// most common producer; <see cref="Stream"/> names a geometry stream, so nothing UI-shaped enters the neutral
/// layer. A producer that has stable streams mints an id for each from <see cref="IEngine.AllocateDrawListStream"/>
/// and bumps a revision when the geometry changes; one that does not passes <see cref="Transient"/>.
/// </para>
/// </summary>
/// <param name="Stream">The id this geometry stream was minted under by <see cref="IEngine.AllocateDrawListStream"/>,
/// stable across frames. Only meaningful when <see cref="IsStable"/>. Minted rather than chosen because the backend
/// keeps one set of buffers per stream: two producers that picked the same number would overwrite each other's
/// geometry, so a stable list naming a stream the engine did not mint is refused.</param>
/// <param name="Revision">A token the producer advances exactly when the stream's geometry changes; equal
/// revisions mean identical bytes. <b>Zero means "no stable identity"</b> and is the sentinel, which is why it is
/// the revision and not the stream that carries it.</param>
public readonly record struct DrawListIdentity(int Stream, uint Revision)
{
	/// <summary>A list with no stable identity: staged fresh every frame, never kept. What a producer that refills
	/// shared scratch buffers between submissions in one frame has to use, because its bytes are gone by the next
	/// call.</summary>
	public static DrawListIdentity Transient => default;

	/// <summary>Whether this names a stream a backend may keep geometry for across frames.</summary>
	public bool IsStable => Revision != 0;
}
