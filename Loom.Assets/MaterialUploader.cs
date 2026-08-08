using System.Runtime.CompilerServices;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Assets;

/// <summary>
/// Turns a loaded <see cref="ModelMaterial"/> into a GPU <see cref="Material"/> for a named technique: uploads the
/// texture channels that technique samples, substitutes a neutral 1×1 stand-in for the ones the model omits, and
/// attaches the scalar factors as the per-material parameter block. This is the missing half of the glTF path —
/// <see cref="GltfLoader"/> decodes a material, and this puts it on the GPU.
/// <para>
/// It lives in <c>Loom.Assets</c> because that project already depends on <c>Loom.Core</c>, where <c>IEngine</c>
/// lives, so no new edge is needed. <c>Loom.Concepts</c> would have needed one, and that would drag SharpGLTF into a
/// package people take for a fly camera.
/// </para>
/// <para>
/// <b>Channel order.</b> Textures are uploaded in glTF's own order — base colour, normal, metallic-roughness,
/// occlusion, emissive — and a technique samples a <em>prefix</em> of it. A technique that reflects one sampler gets
/// base colour; one that reflects five gets all of them. The count comes from
/// <see cref="IEngine.MaterialLayoutOf"/>, so nothing here hand-counts and a shader that gains a sampler is served
/// without changing this code.
/// </para>
/// <para>
/// Instances cache their stand-ins and remember which <see cref="TextureData"/> they have already uploaded, so keep
/// one for the duration of a load. The cache is by reference, which is what matters in practice: glTF materials
/// routinely point metallic-roughness and occlusion at the same image, and the loader hands back the same object for
/// both.
/// </para>
/// </summary>
public sealed class MaterialUploader(IEngine engine)
{
	// glTF's channel order. A technique samples a prefix of this, so the order is a contract with the shader: a
	// two-sampler technique gets base colour then normal, which is what the gallery's g-buffer techniques expect.
	private static readonly string[] ChannelNames = ["base colour", "normal", "metallic-roughness", "occlusion", "emissive"];

	private readonly IEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));
	private readonly Dictionary<TextureData, Texture> _uploaded = new(ReferenceEqualityComparer.Instance);
	private Texture? _white;
	private Texture? _flatNormal;

	/// <summary>
	/// Upload <paramref name="material"/> for <paramref name="technique"/>. The technique must already be defined —
	/// its reflection is what decides how many textures to upload and whether to attach the parameter block.
	/// <para>
	/// Uses the synchronous creation calls, which are not safe to run concurrently with any other GPU-creating call.
	/// Call it from the one thread that services uploads (a demo's synchronous build); from a background load, use
	/// <see cref="UploadAsync"/>.
	/// </para>
	/// </summary>
	public Material Upload(string technique, ModelMaterial material)
	{
		var (layout, textures) = Prepare(technique, material);
		if (!layout.HasParameters)
			return _engine.CreateMaterial(technique, textures);

		var factors = GltfMaterialFactors.From(material);
		return _engine.CreateMaterial(technique, in factors, textures);
	}

	/// <summary>
	/// <see cref="Upload"/> from any thread: queues each upload and completes once the engine services them, so
	/// several models can load concurrently without their GPU calls overlapping.
	/// </summary>
	public async Task<Material> UploadAsync(string technique, ModelMaterial material)
	{
		ArgumentException.ThrowIfNullOrEmpty(technique);
		ArgumentNullException.ThrowIfNull(material);

		var layout = ValidateLayout(technique);
		var textures = new Texture[layout.TextureCount];
		for (var i = 0; i < textures.Length; i++)
			textures[i] = await ResolveAsync(material, i);

		if (!layout.HasParameters)
			return await _engine.CreateMaterialAsync(technique, textures);
		return await _engine.CreateMaterialAsync(technique, GltfMaterialFactors.From(material), textures);
	}

	private (MaterialLayout Layout, Texture[] Textures) Prepare(string technique, ModelMaterial material)
	{
		ArgumentException.ThrowIfNullOrEmpty(technique);
		ArgumentNullException.ThrowIfNull(material);

		var layout = ValidateLayout(technique);
		var textures = new Texture[layout.TextureCount];
		for (var i = 0; i < textures.Length; i++)
			textures[i] = Resolve(material, i);
		return (layout, textures);
	}

	private MaterialLayout ValidateLayout(string technique)
	{
		var layout = _engine.MaterialLayoutOf(technique);
		if (layout.TextureCount > ChannelNames.Length)
		{
			throw new NotSupportedException(
				$"Technique '{technique}' samples {layout.TextureCount} textures, but a glTF metallic-roughness material only defines {ChannelNames.Length} channels ({string.Join(", ", ChannelNames)}). Build this material by hand, or split the extra inputs out of the material.");
		}

		if (layout.HasParameters && layout.ParameterByteSize != Unsafe.SizeOf<GltfMaterialFactors>())
		{
			throw new NotSupportedException(
				$"Technique '{technique}' reads a {layout.ParameterByteSize}-byte material block, but this uploader supplies glTF's {Unsafe.SizeOf<GltfMaterialFactors>()}-byte {nameof(GltfMaterialFactors)}. A shader with its own material struct needs its own upload path — the engine has no opinion about what the bytes mean.");
		}

		return layout;
	}

	// The async twin of Resolve: the same channel choice and the same caches, queuing the upload instead of doing it.
	private async Task<Texture> ResolveAsync(ModelMaterial material, int index)
	{
		var data = ChannelData(material, index);
		if (data is null)
			return index == 1 ? await FlatNormalAsync() : await WhiteAsync();
		if (_uploaded.TryGetValue(data, out var cached))
			return cached;
		var uploaded = await _engine.CreateTextureAsync(data);
		_uploaded[data] = uploaded;
		return uploaded;
	}

	private static TextureData? ChannelData(ModelMaterial material, int index) => index switch
	{
		0 => material.BaseColor,
		1 => material.Normal,
		2 => material.MetallicRoughness,
		3 => material.Occlusion,
		4 => material.Emissive,
		_ => throw new ArgumentOutOfRangeException(nameof(index)),
	};

	// The channel at `index`, or the stand-in that makes its absence a no-op. glTF's rule is that a missing texture
	// leaves the corresponding factor applying unmodified, so every colour-ish channel wants a texel that samples to
	// 1.0 and the normal channel wants one that perturbs nothing.
	private Texture Resolve(ModelMaterial material, int index)
	{
		var data = ChannelData(material, index);
		if (data is null)
			return index == 1 ? FlatNormal() : White();

		if (_uploaded.TryGetValue(data, out var cached))
			return cached;
		var uploaded = _engine.CreateTexture(data);
		_uploaded[data] = uploaded;
		return uploaded;
	}

	// One white texel serves base colour, metallic-roughness, occlusion AND emissive: 255 decodes to 1.0 under both
	// sRGB and UNORM, so the same image is neutral whichever encoding the channel expects, and each channel's factor
	// then decides the result. Emissive falls out correctly too — glTF's default emissive factor is zero, so white
	// times zero is no emission.
	private Texture White() => _white ??= _engine.CreateTexture(WhiteData);

	private async Task<Texture> WhiteAsync() => _white ??= await _engine.CreateTextureAsync(WhiteData);

	private async Task<Texture> FlatNormalAsync() => _flatNormal ??= await _engine.CreateTextureAsync(FlatNormalData);

	private static TextureData WhiteData => TextureData.FromRgba8(1, 1, [255, 255, 255, 255], TextureFormat.Rgba8Unorm, generateMips: false);

	private static TextureData FlatNormalData => TextureData.FromRgba8(1, 1, [128, 128, 255, 255], TextureFormat.Rgba8Unorm, generateMips: false);

	// Tangent-space (0, 0, 1) encoded into unorm: the normal map that leaves the interpolated vertex normal alone.
	private Texture FlatNormal() => _flatNormal ??= _engine.CreateTexture(FlatNormalData);
}
