namespace Loom.Core.Hosting;

/// <summary>
/// Thrown when a pass records with a technique name that was never registered — typically a typo, or a
/// <c>DefineTechnique</c> call that never ran because the content-load path was skipped.
/// </summary>
/// <param name="technique">The unregistered name, as the caller spelled it.</param>
public sealed class TechniqueNotDefinedException(string technique)
	: LoomAuthoringException($"Technique '{technique}' was not defined — call IEngine.DefineTechnique(\"{technique}\", …) before a pass records with it.")
{
	/// <summary>The unregistered technique name.</summary>
	public string Technique { get; } = technique;
}
