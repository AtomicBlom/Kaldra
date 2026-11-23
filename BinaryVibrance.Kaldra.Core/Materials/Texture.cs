using BinaryVibrance.Kaldra.Core.Geometry;

namespace BinaryVibrance.Kaldra.Core.Materials;

/// <summary>
/// An opaque handle to a GPU-resident texture (image + mip chain), minted by the engine when the app uploads
/// <see cref="TextureData"/> and handed back through a <see cref="Material"/> to passes that draw with it. Like
/// <see cref="Mesh"/>, it is just an identity the backend resolves to a real image/view — the describe layer
/// never sees the GPU resource. Textures are app-owned, stable resources (built once at load, kept for the
/// engine's lifetime), so referencing one from a pass's record closure doesn't break the closure's purity the
/// way capturing per-frame data would.
/// </summary>
/// <param name="Id">Stable, engine-assigned index the backend resolves to its texture store entry.</param>
/// <param name="Width">Level-0 width in texels (for HUDs/logs; the backend owns the real dimensions).</param>
/// <param name="Height">Level-0 height in texels.</param>
public readonly record struct Texture(int Id, int Width, int Height);
