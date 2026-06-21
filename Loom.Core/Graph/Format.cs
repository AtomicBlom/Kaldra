using Loom.Core.Materials;

namespace Loom.Core.Graph;

/// <summary>
/// Backend-neutral render-graph <b>attachment / target</b> pixel formats — an <em>abstract</em> vocabulary the
/// backend maps to a concrete API format (e.g. <c>Rgba8Srgb</c> → <c>VK_FORMAT_R8G8B8A8_SRGB</c>). Keep this list
/// free of any graphics-API type so <c>Loom.Core</c> stays portable. Grow it deliberately as effects need more.
/// <para>
/// Deliberately distinct from <see cref="TextureFormat"/> (the source-image / sampled-encoding vocabulary): the
/// two overlap on the plain RGBA8 encodings on purpose, but diverge as render targets gain HDR/packed/depth
/// formats while source textures gain block-compressed ones. Keep them separate rather than unifying.
/// </para>
/// </summary>
public enum Format
{
	/// <summary>8-bit RGBA, sRGB-encoded. Default for albedo / colour the eye reads directly.</summary>
	Rgba8Srgb,

	/// <summary>8-bit RGBA, linear. For data that isn't colour (packed material params, etc.).</summary>
	Rgba8Unorm,

	/// <summary>10/10/10/2 packed. Cheap, higher-precision normals.</summary>
	Rgb10A2,

	/// <summary>16-bit float RGBA. HDR colour with enough range for bloom.</summary>
	Rgba16F,

	/// <summary>16-bit float, single channel. Single-channel data: SSAO, the exposure scalar, and the
	/// reference-frame's object-id picking target (small ids are exact in F16 — see <see cref="R32Uint"/>).</summary>
	R16F,

	/// <summary>32-bit unsigned integer, single channel. The format for **true** integer object ids — exact
	/// for any id range, where <see cref="R16F"/> is only exact below 2048. (The reference frame's picking path
	/// uses <see cref="R16F"/>: its ids are tiny and a float id composes directly with the float-based JFA
	/// outline. Switch the id target to this when an app needs large/precise picking ids.)</summary>
	R32Uint,

	/// <summary>16-bit float, two channels. Velocity / JFA seed coordinates.</summary>
	Rg16F,

	/// <summary>32-bit float depth.</summary>
	Depth32F,
}
