using BinaryVibrance.Kaldra.Core.Graph;

namespace BinaryVibrance.Kaldra.Graph;

/// <summary>
/// Interprets each <see cref="AccessKind"/> as a read and/or a write for dependency derivation. This is
/// Execute-layer semantics, deliberately kept out of <c>BinaryVibrance.Kaldra.Core</c> (which only <em>declares</em> intent).
/// The load-bearing case is <see cref="AccessKind.Blend"/> / <see cref="AccessKind.ReadWrite"/>: both read
/// <em>and</em> write, so an accumulation pass depends on the prior writer (RAW) and blocks later writers
/// (WAR) at once.
/// </summary>
internal static class AccessKindExtensions
{
	public static bool Reads(this AccessKind kind) => kind switch
	{
		AccessKind.Sample => true,
		AccessKind.ReadDepth => true,
		AccessKind.Blend => true,
		AccessKind.ReadWrite => true,
		AccessKind.ReadVertex => true,
		AccessKind.ReadIndirect => true,
		AccessKind.ReadUniform => true,
		AccessKind.ReadStorage => true,
		_ => false,
	};

	public static bool Writes(this AccessKind kind) => kind switch
	{
		AccessKind.Write => true,
		AccessKind.WriteDepth => true,
		AccessKind.Blend => true,
		AccessKind.ReadWrite => true,
		_ => false,
	};
}
