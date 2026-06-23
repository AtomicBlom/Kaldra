namespace Loom.Sandbox.Demos;

/// <summary>
/// The string keys the sandbox uses for its named views (<c>FrameInputs.Views</c> / <c>ctx.Views</c>). The engine
/// names no viewpoint itself, so the host and the demos agree on these app-side: the host fills the views under
/// these keys, and a demo's pass closures pull them back by the same key. Cached so a per-frame lookup allocates
/// no string.
/// </summary>
internal static class ViewKeys
{
	/// <summary>The shadow-casting light's view — a single shadow map's light-space camera.</summary>
	public const string Sun = "sun";

	private static readonly string[] CascadeKeys = ["csm0", "csm1", "csm2", "csm3"];

	/// <summary>The key for cascade <paramref name="index"/> of a cascaded shadow map (nearest cascade is 0).</summary>
	public static string Cascade(int index) => CascadeKeys[index];
}
