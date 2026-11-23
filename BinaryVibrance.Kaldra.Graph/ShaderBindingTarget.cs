namespace BinaryVibrance.Kaldra.Graph;

/// <summary>
/// Which compilation target's numbering a reflection's binding indices belong to.
/// <para>
/// The shader build emits a reflection sidecar per target, and they are <em>different index spaces for the same
/// source</em>: the SPIR-V pass honours the shader's explicit slots, while the WGSL pass lets Slang auto-number in
/// declaration order and splits a combined <c>Sampler2D</c> into two slots. So the same parameter can be index 0 in
/// one sidecar and index 3 in the other. An index is meaningless without knowing which — a backend that read the
/// wrong sidecar would build a layout that looks entirely plausible and binds the wrong resources.
/// </para>
/// </summary>
public enum ShaderBindingTarget
{
	/// <summary>No sidecar was parsed, so there are no indices to interpret.</summary>
	None = 0,

	/// <summary>The SPIR-V sidecar: the shader's own explicit slot numbers. What the desktop backends bind against.</summary>
	SpirV,

	/// <summary>The WGSL sidecar: slots Slang assigned in declaration order.</summary>
	Wgsl,
}
