using System.Runtime.Versioning;

using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Backend.WebGPU;

/// <summary>
/// Owns app-uploaded sampled textures: a GPU texture with its full (CPU-generated) mip chain, uploaded once at
/// creation via <c>queue.writeTexture</c> (no 256-byte row constraint, unlike a texture→buffer copy). The mips
/// come pre-built and byte-identical across backends from <see cref="TextureData"/>, so this store only uploads
/// them. A material bundles their handles and the draw path resolves them to the views a bind group samples. A
/// texture lives until teardown or until the app releases it.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class WebGpuTextureStore(WebGpuHandleAllocator handles) : IDisposable
{
	private struct StoredTexture
	{
		public int Texture;
		public int View;
		public bool Released; // the id's slot after IEngine.ReleaseTexture: no handles, and Slot refuses it
	}

	private readonly List<StoredTexture> _textures = [];

	/// <summary>
	/// Resolve a <see cref="Texture.Id"/> to its slot in the store. Ids are 1-based so <c>default(Texture)</c> (id 0) is
	/// never a live handle: sampling an unassigned <see cref="Texture"/> field fails here instead of silently binding
	/// whichever texture was uploaded first. A released texture keeps its slot, emptied, so its stale handle fails here
	/// too — which matters more here than on the native backends, because the view handle it held is recycled.
	/// </summary>
	private int Slot(int textureId)
	{
		if (textureId < 1 || textureId > _textures.Count)
		{
			throw new WebGpuException(
				$"Texture id {textureId} was not created by this engine ({(textureId == 0 ? "0 is default(Texture), an unassigned handle" : $"only ids 1..{_textures.Count} exist")}). " +
				"Textures come from IEngine.CreateTexture / CreateTextureAsync.");
		}
		if (_textures[textureId - 1].Released)
			throw new WebGpuException($"Texture id {textureId} was released by IEngine.ReleaseTexture; its handle can no longer be sampled or released.");
		return textureId - 1;
	}

	/// <summary>Upload a texture + its mip chain and return a handle a material can bundle.</summary>
	public Texture Create(TextureData data)
	{
		if (data.IsCube)
			throw new WebGpuException("Cube textures are not yet supported on the WebGPU backend.");
		// Ids are never reused, and from DebugTextureBase up they name graph resources instead; refuse before allocating.
		if (_textures.Count + 1 >= InspectableResource.DebugTextureBase)
		{
			throw new WebGpuException(
				$"This engine has minted {_textures.Count} texture ids, the most it can: ids are never reused, so that a released " +
				$"texture's stale handle fails rather than naming its successor, and ids from {InspectableResource.DebugTextureBase} " +
				"up name graph resources for the resource viewer.");
		}

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
		return new Texture(_textures.Count, data.Width, data.Height);
	}

	/// <summary>The sampled view handle of an uploaded texture.</summary>
	public int View(int textureId) => _textures[Slot(textureId)].View;

	/// <summary>
	/// Release a texture: its id stops resolving and its handles are released at once, which destroys the texture.
	/// WebGPU defines <c>destroy()</c> on a texture that submitted work still reads as taking effect once that work
	/// completes, and <c>queue.writeTexture</c> is ordered ahead of later submissions, so unlike the native backends
	/// nothing here has to wait for frames in flight. The view handle goes back to the allocator, which gives it to the
	/// next view created — so whatever is keyed by it must already have been evicted.
	/// </summary>
	public void Release(int textureId)
	{
		var slot = Slot(textureId);
		handles.Release(WebGpuHandleAllocator.Views, _textures[slot].View);
		handles.Release(WebGpuHandleAllocator.Textures, _textures[slot].Texture);
		_textures[slot] = new StoredTexture { Released = true };
	}

	public void Dispose()
	{
		foreach (var texture in _textures)
		{
			if (texture.Released)
				continue;
			handles.Release(WebGpuHandleAllocator.Views, texture.View);
			handles.Release(WebGpuHandleAllocator.Textures, texture.Texture);
		}
		_textures.Clear();
	}
}
