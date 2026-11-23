namespace BinaryVibrance.Kaldra.Core.Hosting;

/// <summary>
/// Thrown when a <see cref="DrawCall"/> is internally inconsistent — most often a procedural draw (<c>Mesh</c> left
/// null) that supplies neither a <c>VertexCount</c> nor an instance source, so there is nothing to draw and nothing
/// to derive a vertex count from.
/// </summary>
/// <param name="pass">The pass that was recording.</param>
/// <param name="technique">The technique the draw named.</param>
/// <param name="message">The rest of the message; the <c>Pass '…': </c> prefix is added here.</param>
public sealed class InvalidDrawCallException(string pass, string technique, string message)
	: KaldraAuthoringException($"Pass '{pass}': {message}")
{
	/// <summary>The pass that issued the draw.</summary>
	public string Pass { get; } = pass;

	/// <summary>The technique the draw named.</summary>
	public string Technique { get; } = technique;
}
