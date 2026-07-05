using System.Runtime.Versioning;

using Loom.Core.Materials;

namespace Loom.Backend.WebGPU;

/// <summary>
/// Owns app-uploaded sampled textures: a GPU texture with its full (CPU-generated) mip chain, uploaded once at
/// creation via <c>queue.writeTexture</c> (no 256-byte row constraint, unlike a texture→buffer copy). The mips
/// come pre-built and byte-identical across backends from <see cref="TextureData"/>, so this store only uploads
/// them. Textures are long-lived app resources; a material bundles their handles and the draw path resolves them
/// to the views a bind group samples.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class WebGpuTextureStore(WebGpuHandleAllocator handles) : IDisposable
{
	private struct StoredTexture
	{
		public int Texture;
		public int View;
	}

	private readonly List<StoredTexture> _textures = [];

	/// <summary>Upload a texture + its mip chain and return a handle a material can bundle.</summary>
	public Texture Create(TextureData data)
	{
		if (data.IsCube)
			throw new WebGpuException("Cube textures are not yet supported on the WebGPU backend.");

		var formatId = WebGpuFormatMapping.ToWebGpu(data.Format);
		var bytesPerTexel = WebGpuFormatMapping.BytesPerTexel(data.Format);
		var mipCount = data.MipsPerFace;

		var texture = handles.Allocate(WebGpuHandleAllocator.Textures);
		WebGpuInterop.CreateTexture(texture, data.Width, data.Height, mipCount, formatId,
			WebGpuStateMapping.TextureBinding | WebGpuStateMapping.TextureCopyDst, sampleCount: 1, "texture");

		for (var mip = 0; mip < mipCount; mip++)
		{
			var level = data.Levels[mip];
			WebGpuInterop.WriteTexture(texture, mip, level.Width, level.Height, level.Width * bytesPerTexel, level.Pixels);
		}

		var view = handles.Allocate(WebGpuHandleAllocator.Views);
		WebGpuInterop.CreateTextureView(view, texture, formatId, baseMip: 0, mipCount, "texture");

		_textures.Add(new StoredTexture { Texture = texture, View = view });
		return new Texture(_textures.Count - 1, data.Width, data.Height);
	}

	/// <summary>The sampled view handle of an uploaded texture.</summary>
	public int View(int textureId) => _textures[textureId].View;

	public void Dispose()
	{
		foreach (var texture in _textures)
		{
			handles.Release(WebGpuHandleAllocator.Views, texture.View);
			handles.Release(WebGpuHandleAllocator.Textures, texture.Texture);
		}
		_textures.Clear();
	}
}
