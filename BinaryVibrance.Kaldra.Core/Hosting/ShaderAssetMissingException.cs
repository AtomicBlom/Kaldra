namespace BinaryVibrance.Kaldra.Core.Hosting;

/// <summary>
/// Thrown when a technique's compiled shader or its reflection sidecar cannot be resolved — neither embedded in an
/// assembly nor present on disk. Almost always a build or packaging slip rather than a code error: the shader project
/// did not run, or the compiled output did not travel with the app.
/// <para>
/// This throws rather than substituting a stand-in shader. A stand-in shades differently, so falling back would
/// produce a <em>different image on one backend with no error at all</em> — the silent divergence the engine's
/// fail-loud posture exists to prevent.
/// </para>
/// </summary>
/// <param name="shader">The shader name that could not be resolved.</param>
/// <param name="message">The full, already-composed message, naming every location that was tried.</param>
public sealed class ShaderAssetMissingException(string shader, string message) : KaldraAuthoringException(message)
{
	/// <summary>The shader name that could not be resolved.</summary>
	public string Shader { get; } = shader;
}
