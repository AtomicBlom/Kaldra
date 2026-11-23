namespace BinaryVibrance.Kaldra.Core.Graph;

/// <summary>
/// The test a technique applies between a fragment's depth and the value already in the depth buffer — the fragment
/// survives when the comparison holds. Fixed-function pipeline state the backend bakes in, like <see cref="BlendMode"/>
/// and <see cref="CullMode"/>: intent reflection can't infer, so the technique states it.
/// <para>
/// The default, <see cref="LessOrEqual"/>, is the conventional depth buffer: near is 0, far is 1, the target clears
/// to 1, and a nearer fragment wins. The reason this is configurable is <b>reversed-Z</b>: clear the depth target to
/// 0 (<c>Domain.Depth(name, format, clearDepth: 0f)</c>), project with
/// <c>Camera.Perspective(…, reversed: true)</c>, and test <see cref="GreaterOrEqual"/>. That pairs a float depth
/// buffer's dense exponent range near 0 with the far plane instead of the near plane, which is where a perspective
/// divide throws precision away — the standard fix for depth fighting in a large world. All three pieces must agree;
/// changing only one silently inverts or erases occlusion rather than failing.
/// </para>
/// </summary>
public enum DepthCompare
{
	/// <summary>No fragment ever passes.</summary>
	Never,

	/// <summary>Pass when the fragment is strictly nearer than the stored depth.</summary>
	Less,

	/// <summary>Pass only at exactly the stored depth — a depth-equal pass re-shading geometry a prepass already laid down.</summary>
	Equal,

	/// <summary>Pass when the fragment is nearer than or equal to the stored depth. The conventional default.</summary>
	LessOrEqual,

	/// <summary>Pass when the fragment is strictly further than the stored depth.</summary>
	Greater,

	/// <summary>Pass at any depth but exactly the stored one.</summary>
	NotEqual,

	/// <summary>Pass when the fragment is further than or equal to the stored depth. The reversed-Z counterpart of <see cref="LessOrEqual"/>.</summary>
	GreaterOrEqual,

	/// <summary>Every fragment passes — the test is off, though depth writes still happen if the pass writes depth.</summary>
	Always,
}
