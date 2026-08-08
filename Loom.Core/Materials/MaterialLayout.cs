namespace Loom.Core.Materials;

/// <summary>
/// What a technique expects a <see cref="Material"/> to supply: how many textures it samples and how many bytes of
/// per-material parameters it reads. Both are <em>derived from the shader's reflection</em>, never declared — the
/// engine names no material model, so there is no PBR struct here and no opinion about what the bytes mean. A shader
/// that wants base colour, metallic, and roughness declares a constant buffer of its own shape; this type only
/// records its size so the pairing can be checked and the block staged.
/// <para>
/// The point of having it at all is that a material and a technique are two independent declarations that must
/// agree. Comparing layouts turns "the shader reads 48 bytes but this material carries 32" into an error at
/// <c>CreateMaterial</c> or <c>DefineTechnique</c> — including on hot reload, so reordering or resizing a block in a
/// shader fails loudly at the reload rather than silently shading from stale bytes.
/// </para>
/// </summary>
/// <param name="TextureCount">Sampled textures the technique binds — its reflected sampler-slot count.</param>
/// <param name="ParameterByteSize">Bytes of per-material parameters the technique reads, as the shader lays the block out (padded to its constant-buffer alignment). 0 when the technique declares no material block.</param>
public readonly record struct MaterialLayout(int TextureCount, int ParameterByteSize)
{
	/// <summary>Whether the technique reads a per-material parameter block at all.</summary>
	public bool HasParameters => ParameterByteSize > 0;

	/// <summary>
	/// Whether a material carrying <paramref name="textureCount"/> textures and <paramref name="parameterByteSize"/>
	/// bytes of parameters can drive a technique with this layout. Extra textures are allowed — a material may carry
	/// more than a given technique samples, which is how one material feeds both a G-buffer pass and a
	/// depth-only pass that reflects no samplers at all. Parameter bytes must match exactly: a short block would let
	/// the shader read past it, and a long one means the two disagree about the struct.
	/// </summary>
	public bool Accepts(int textureCount, int parameterByteSize) =>
		textureCount >= TextureCount && parameterByteSize == ParameterByteSize;
}
