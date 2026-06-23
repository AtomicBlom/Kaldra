using Loom.Core.Geometry;
using Loom.Core.Hosting;

namespace Loom.Core.Materials;

/// <summary>
/// An opaque handle to an ordered bundle of <see cref="Texture"/>s, minted by the engine via
/// <see cref="IEngine.CreateMaterial"/> and handed to a draw through
/// <see cref="IRenderContext.Draw"/>. The material supplies the textures that fill a
/// technique's reflected sampler slots, in declaration order (texture 0 → the shader's binding 0, and so on);
/// the <em>technique</em> still selects the pipeline, so the same material can be drawn into a G-buffer pass and
/// skipped by a depth-only/shadow pass (which reflects no samplers) without change. Like <see cref="Mesh"/> and
/// <see cref="Texture"/> it is just an identity the backend resolves — the describe layer never sees the GPU
/// resources, and the ordered texture list lives backend-side so the draw path binds with zero allocation.
/// <para>
/// A material is textures only. A per-material scalar/parameter block (tints, roughness factors)
/// can be added without changing this handle.
/// </para>
/// </summary>
/// <param name="Id">Stable, engine-assigned index the backend resolves to its material (texture-id list) entry.</param>
public readonly record struct Material(int Id);
