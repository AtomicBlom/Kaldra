using BinaryVibrance.Kaldra.Core.Geometry;
using BinaryVibrance.Kaldra.Core.Hosting;

namespace BinaryVibrance.Kaldra.Core.Materials;

/// <summary>
/// An opaque handle to an ordered bundle of <see cref="Texture"/>s, minted by the engine via
/// <see cref="IEngine.CreateMaterial(ReadOnlySpan{Texture})"/> and handed to a draw through
/// <see cref="IRenderContext.Draw"/>. The material supplies the textures that fill a
/// technique's reflected sampler slots, in declaration order (texture 0 → the shader's binding 0, and so on);
/// the <em>technique</em> still selects the pipeline, so the same material can be drawn into a G-buffer pass and
/// skipped by a depth-only/shadow pass (which reflects no samplers) without change. Like <see cref="Mesh"/> and
/// <see cref="Texture"/> it is just an identity the backend resolves — the describe layer never sees the GPU
/// resources, and the ordered texture list lives backend-side so the draw path binds with zero allocation.
/// <para>
/// A material may also carry a <em>parameter block</em> — tints, roughness factors, whatever its shader declares —
/// when built with <see cref="IEngine.CreateMaterial{T}"/> against a technique whose shader declares a
/// <c>kaldraMaterial</c> constant buffer. The engine holds those bytes on the CPU and stages them per draw, so the
/// handle stays an identity and <see cref="IEngine.Write{T}(Material, in T)"/> can rewrite them without touching the
/// graph. The engine names no material model: the struct's meaning is entirely the shader's.
/// </para>
/// </summary>
/// <param name="Id">Stable, engine-assigned index the backend resolves to its material entry (texture ids + parameter bytes).</param>
public readonly record struct Material(int Id);
