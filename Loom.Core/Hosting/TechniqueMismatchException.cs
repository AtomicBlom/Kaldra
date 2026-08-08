namespace Loom.Core.Hosting;

/// <summary>
/// Thrown when a draw contradicts the shape its technique declared or its shader reflected — a material carrying the
/// wrong texture or parameter-block size, a mesh handed to a technique that fabricates its own vertices, or a push
/// that no channel of the technique can carry.
/// <para>
/// The message is composed by the caller because the mismatches read differently, but they share a type so an app can
/// handle "this draw and this technique disagree" without discriminating on prose.
/// </para>
/// </summary>
/// <param name="message">The full, already-composed message.</param>
/// <param name="technique">The technique the draw named.</param>
public sealed class TechniqueMismatchException(string message, string technique) : LoomAuthoringException(message)
{
	/// <summary>The technique whose declared or reflected shape the draw contradicted.</summary>
	public string Technique { get; } = technique;
}
