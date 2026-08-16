namespace Loom.Core.Hosting;

/// <summary>
/// Identity of a draw-list geometry stream re-submitted across frames — the neutral handle a backend keys kept
/// vertex and index buffers by, so an unchanged list is not re-uploaded every frame.
/// <para>
/// Deliberately not named for the UI. A draw list is the engine's batched-geometry verb and a UI surface is only its
/// most common producer; <see cref="Stream"/> names a geometry stream, so nothing UI-shaped enters the neutral
/// layer. A producer that has stable streams supplies its own ids and a revision it bumps when the geometry
/// changes; one that does not passes <see cref="Transient"/>.
/// </para>
/// </summary>
/// <param name="Stream">The producer's id for this geometry stream, stable across frames. Only meaningful when
/// <see cref="IsStable"/> — two producers' ids may collide, so a producer that mints ids from zero is fine as long
/// as it is the only one submitting stable streams.</param>
/// <param name="Revision">A token the producer advances exactly when the stream's geometry changes; equal
/// revisions mean identical bytes. <b>Zero means "no stable identity"</b> and is the sentinel, which is why it is
/// the revision and not the stream that carries it: a producer numbering surfaces from zero would otherwise have
/// its first surface read as transient.</param>
public readonly record struct DrawListIdentity(int Stream, uint Revision)
{
	/// <summary>A list with no stable identity: staged fresh every frame, never kept. What a producer that refills
	/// shared scratch buffers between submissions in one frame has to use, because its bytes are gone by the next
	/// call.</summary>
	public static DrawListIdentity Transient => default;

	/// <summary>Whether this names a stream a backend may keep geometry for across frames.</summary>
	public bool IsStable => Revision != 0;
}
