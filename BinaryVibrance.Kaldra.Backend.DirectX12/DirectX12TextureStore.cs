using System.Runtime.InteropServices;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;
using BinaryVibrance.Kaldra.Graph;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using DxgiFormat = Silk.NET.DXGI.Format;

namespace BinaryVibrance.Kaldra.Backend.DirectX12;

/// <summary>
/// Owns GPU-resident textures — the DirectX12 counterpart of the Vulkan backend's <c>VulkanTextureStore</c>.
/// Each texture is a DEFAULT-heap committed <c>Texture2D</c> with a full mip chain,
/// uploaded once from an UPLOAD-heap staging buffer via per-subresource <c>CopyTextureRegion</c>, then left in
/// <c>PixelShaderResource</c>. Mip levels arrive pre-built on the CPU in <see cref="TextureData"/>, so the
/// store just lays each level into the staging buffer at the footprint <c>GetCopyableFootprints</c> reports.
/// The copies are recorded into the shared <see cref="DirectX12UploadBatch"/>, which executes them ahead of the
/// frame that samples them rather than blocking on each one. A texture lives until teardown or, when the app releases
/// it, until the frame fence passes the last frame that can read it.
/// </summary>
internal sealed unsafe class DirectX12TextureStore : IDisposable
{
	private struct StoredTexture
	{
		public ComPtr<ID3D12Resource> Resource;
		public DxgiFormat Format;
		public uint MipLevels;
		public bool IsCube;
		public bool Released; // the id's slot after IEngine.ReleaseTexture: no resource, and Slot refuses it

		// Its shape, which an update is held to (TextureUpdates.Check).
		public int Width, Height, BytesPerTexel;

		// The upload-heap buffer its updates are staged in, laid out as its one subresource's copyable footprint: made by
		// the first update and kept until the texture goes, so an update every frame allocates nothing. Null until then.
		public ComPtr<ID3D12Resource> Staging;
		public PlacedSubresourceFootprint StagingFootprint;
		public uint StagingRows;
		public ulong StagingRowBytes;
	}

	private readonly DirectX12Context _ctx;
	private readonly List<StoredTexture> _textures = [];
	// Released textures whose resources a frame in flight (or a pending upload) may still read, each with the frame-fence
	// value signalled after the last work that can.
	private readonly List<(StoredTexture Texture, ulong LastReadingFence)> _releasing = [];

	private readonly DirectX12UploadBatch _uploads;

	public DirectX12TextureStore(DirectX12Context ctx, DirectX12UploadBatch uploads)
	{
		_ctx = ctx;
		_uploads = uploads;
	}

	/// <summary>
	/// Resolve a <see cref="Texture.Id"/> to its slot in the store. Ids are 1-based so <c>default(Texture)</c> (id 0) is
	/// never a live handle: sampling an unassigned <see cref="Texture"/> field fails here instead of silently binding
	/// whichever texture was uploaded first. A released texture keeps its slot, emptied, so its stale handle fails here
	/// too.
	/// </summary>
	private int Slot(int textureId)
	{
		if (textureId < 1 || textureId > _textures.Count)
		{
			throw new InvalidOperationException(
				$"DirectX12: texture id {textureId} was not created by this engine ({(textureId == 0 ? "0 is default(Texture), an unassigned handle" : $"only ids 1..{_textures.Count} exist")}). " +
				"Textures come from IEngine.CreateTexture / CreateTextureAsync.");
		}
		if (_textures[textureId - 1].Released)
			throw new InvalidOperationException($"DirectX12: texture id {textureId} was released by IEngine.ReleaseTexture; its handle can no longer be sampled or released.");
		return textureId - 1;
	}

	/// <summary>Upload a texture + its mip chain and return a handle a material can bundle.</summary>
	public Texture Create(TextureData data)
	{
		// Ids are never reused, and from DebugTextureBase up they name graph resources instead; refuse before allocating.
		if (_textures.Count + 1 >= InspectableResource.DebugTextureBase)
		{
			throw new InvalidOperationException(
				$"DirectX12: this engine has minted {_textures.Count} texture ids, the most it can: ids are never reused, so that a " +
				$"released texture's stale handle fails rather than naming its successor, and ids from {InspectableResource.DebugTextureBase} " +
				"up name graph resources for the resource viewer.");
		}

		var format = data.Format switch
		{
			TextureFormat.Rgba8Srgb => DxgiFormat.FormatR8G8B8A8UnormSrgb,
			TextureFormat.Rgba8Unorm => DxgiFormat.FormatR8G8B8A8Unorm,
			TextureFormat.Rgba16F => DxgiFormat.FormatR16G16B16A16Float,
			_ => throw new NotSupportedException($"No DirectX12 texture format mapping for {data.Format}."),
		};
		// A cube map is six array slices viewed as a TextureCube; the per-slice mip count is MipsPerFace and the
		// total subresource count (slices × mips, face-major like the Levels list) drives the footprints + copies.
		var mipsPerFace = (uint)data.MipsPerFace;
		var arraySize = (ushort)data.LayerCount;
		var subresourceCount = (uint)data.Levels.Count;

		// DEFAULT-heap committed texture with the full mip chain, created ready to receive the copy.
		var defaultHeap = new HeapProperties { Type = HeapType.Default, CPUPageProperty = CpuPageProperty.Unknown, MemoryPoolPreference = MemoryPool.Unknown, CreationNodeMask = 1, VisibleNodeMask = 1 };
		var desc = new ResourceDesc
		{
			Dimension = ResourceDimension.Texture2D,
			Alignment = 0,
			Width = (ulong)data.Width,
			Height = (uint)data.Height,
			DepthOrArraySize = arraySize,
			MipLevels = (ushort)mipsPerFace,
			Format = format,
			SampleDesc = new Silk.NET.DXGI.SampleDesc { Count = 1, Quality = 0 },
			Layout = TextureLayout.LayoutUnknown,
			Flags = ResourceFlags.None,
		};
		ComPtr<ID3D12Resource> texture = default;
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommittedResource(
			&defaultHeap, HeapFlags.None, &desc, ResourceStates.CopyDest, (ClearValue*)null, out texture));

		// Footprints: where each subresource lands in the staging buffer (256-aligned row pitch, per-subresource offset).
		var layouts = stackalloc PlacedSubresourceFootprint[(int)subresourceCount];
		var numRows = stackalloc uint[(int)subresourceCount];
		var rowSizes = stackalloc ulong[(int)subresourceCount];
		ulong totalBytes;
		_ctx.Device.GetCopyableFootprints(&desc, 0, subresourceCount, 0, layouts, numRows, rowSizes, &totalBytes);

		var staging = CreateUploadBuffer(totalBytes);
		void* mapped;
		var noRead = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };
		SilkMarshal.ThrowHResult(staging.Map(0, &noRead, &mapped));
		for (var i = 0; i < data.Levels.Count; i++)
		{
			var level = data.Levels[i];
			var srcPitch = level.Pixels.Length / level.Height; // tightly packed source row (bytes-per-texel from the level)
			var dstPitch = (int)layouts[i].Footprint.RowPitch; // 256-aligned
			var rowBytes = (int)rowSizes[i];                // unpadded bytes per row
			fixed (byte* src = level.Pixels)
				for (var r = 0; r < numRows[i]; r++)
					System.Buffer.MemoryCopy(src + r * srcPitch, (byte*)mapped + layouts[i].Offset + (ulong)(r * dstPitch), rowBytes, rowBytes);
		}
		staging.Unmap(0, (Silk.NET.Direct3D12.Range*)null);

		// Recorded into the shared upload batch rather than a list of its own: lay each subresource in, then
		// transition the whole texture to a shader resource. The batch executes before the frame that samples it, so
		// nothing has to wait here.
		var cmd = _uploads.Recorder();
		for (var i = 0u; i < subresourceCount; i++)
		{
			var dst = new TextureCopyLocation
			{
				PResource = texture.Handle,
				Type = TextureCopyType.SubresourceIndex,
				Anonymous = new TextureCopyLocationUnion { SubresourceIndex = i },
			};
			var src = new TextureCopyLocation
			{
				PResource = staging.Handle,
				Type = TextureCopyType.PlacedFootprint,
				Anonymous = new TextureCopyLocationUnion { PlacedFootprint = layouts[i] },
			};
			cmd.CopyTextureRegion(&dst, 0, 0, 0, &src, (Box*)null);
		}
		var barrier = new ResourceBarrier
		{
			Type = ResourceBarrierType.Transition,
			Flags = ResourceBarrierFlags.None,
			Anonymous = new ResourceBarrierUnion
			{
				Transition = new ResourceTransitionBarrier
				{
					PResource = texture.Handle,
					Subresource = 0xffffffff, // all subresources
					StateBefore = ResourceStates.CopyDest,
					StateAfter = ResourceStates.PixelShaderResource,
				},
			},
		};
		cmd.ResourceBarrier(1, &barrier);
		_uploads.Retire(staging);

		_textures.Add(new StoredTexture
		{
			Resource = texture,
			Format = format,
			MipLevels = mipsPerFace,
			IsCube = data.IsCube,
			Width = data.Width,
			Height = data.Height,
			BytesPerTexel = data.Levels[0].Pixels.Length / (data.Width * data.Height),
		});
		return new Texture(_textures.Count, data.Width, data.Height);
	}

	/// <summary>The texture resource a material binding creates an SRV over.</summary>
	public ComPtr<ID3D12Resource> Resource(int textureId) => _textures[Slot(textureId)].Resource;

	/// <summary>The DXGI format an SRV over this texture uses.</summary>
	public DxgiFormat SrvFormat(int textureId) => _textures[Slot(textureId)].Format;

	/// <summary>The texture's mip-level count (so the SRV exposes the whole chain, not just mip 0).</summary>
	public uint MipLevels(int textureId) => _textures[Slot(textureId)].MipLevels;

	/// <summary>True when the texture is a cube map (six array slices), so the SRV is created as a <c>TextureCube</c>.</summary>
	public bool IsCube(int textureId) => _textures[Slot(textureId)].IsCube;

	/// <summary>
	/// Copy <paramref name="texels"/> into a texture's resource in place, through the upload batch, ahead of the next
	/// frame. The resource is not replaced, so the SRVs naming it stay. Its staging buffer is made by the first update and
	/// kept, so later ones allocate nothing.
	/// <para>
	/// Two hazards, both answered by queue order rather than a wait. The staging buffer is rewritten while the last batch
	/// may still be copying from it, so the batch is opened first: opening one waits out the one before, which was
	/// executed a frame ago and has long finished. And frames already executed may still sample the texture, so the copy
	/// is bracketed by transitions out of and back into <c>PixelShaderResource</c>: a transition waits for the queue's
	/// earlier work on the resource, so the write follows those reads on the GPU.
	/// </para>
	/// </summary>
	public void Write(int textureId, ReadOnlySpan<byte> texels)
	{
		var slot = Slot(textureId);
		var texture = _textures[slot];
		TextureUpdates.Check(textureId, texture.Width, texture.Height, texture.BytesPerTexel, (int)texture.MipLevels, texture.IsCube ? 6 : 1, texels.Length);

		var cmd = _uploads.Recorder();
		if (texture.Staging.Handle is null)
		{
			var desc = new ResourceDesc
			{
				Dimension = ResourceDimension.Texture2D,
				Alignment = 0,
				Width = (ulong)texture.Width,
				Height = (uint)texture.Height,
				DepthOrArraySize = 1,
				MipLevels = 1,
				Format = texture.Format,
				SampleDesc = new Silk.NET.DXGI.SampleDesc { Count = 1, Quality = 0 },
				Layout = TextureLayout.LayoutUnknown,
				Flags = ResourceFlags.None,
			};
			PlacedSubresourceFootprint footprint;
			uint rows;
			ulong rowBytes, totalBytes;
			_ctx.Device.GetCopyableFootprints(&desc, 0, 1, 0, &footprint, &rows, &rowBytes, &totalBytes);
			texture.Staging = CreateUploadBuffer(totalBytes);
			texture.StagingFootprint = footprint;
			texture.StagingRows = rows;
			texture.StagingRowBytes = rowBytes;
			_textures[slot] = texture;
		}

		void* mapped;
		var noRead = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };
		SilkMarshal.ThrowHResult(texture.Staging.Map(0, &noRead, &mapped));
		var srcPitch = texture.Width * texture.BytesPerTexel;
		var dstPitch = (int)texture.StagingFootprint.Footprint.RowPitch;
		var rowSize = (int)texture.StagingRowBytes;
		fixed (byte* src = texels)
		{
			for (var r = 0; r < texture.StagingRows; r++)
				System.Buffer.MemoryCopy(src + r * srcPitch, (byte*)mapped + texture.StagingFootprint.Offset + (ulong)(r * dstPitch), rowSize, rowSize);
		}
		texture.Staging.Unmap(0, (Silk.NET.Direct3D12.Range*)null);

		Transition(cmd, texture.Resource, ResourceStates.PixelShaderResource, ResourceStates.CopyDest);
		var dst = new TextureCopyLocation
		{
			PResource = texture.Resource.Handle,
			Type = TextureCopyType.SubresourceIndex,
			Anonymous = new TextureCopyLocationUnion { SubresourceIndex = 0 },
		};
		var source = new TextureCopyLocation
		{
			PResource = texture.Staging.Handle,
			Type = TextureCopyType.PlacedFootprint,
			Anonymous = new TextureCopyLocationUnion { PlacedFootprint = texture.StagingFootprint },
		};
		cmd.CopyTextureRegion(&dst, 0, 0, 0, &source, (Box*)null);
		Transition(cmd, texture.Resource, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
	}

	private static void Transition(ComPtr<ID3D12GraphicsCommandList> cmd, ComPtr<ID3D12Resource> resource, ResourceStates before, ResourceStates after)
	{
		var barrier = new ResourceBarrier
		{
			Type = ResourceBarrierType.Transition,
			Flags = ResourceBarrierFlags.None,
			Anonymous = new ResourceBarrierUnion
			{
				Transition = new ResourceTransitionBarrier
				{
					PResource = resource.Handle,
					Subresource = 0xffffffff, // all subresources
					StateBefore = before,
					StateAfter = after,
				},
			},
		};
		cmd.ResourceBarrier(1, &barrier);
	}

	/// <summary>
	/// Release a texture: its id stops resolving at once, and its resource waits until the frame fence reaches
	/// <paramref name="lastReadingFence"/>, the value signalled after the last frame — and the last upload batch — that
	/// may still read it.
	/// </summary>
	public void Release(int textureId, ulong lastReadingFence)
	{
		var slot = Slot(textureId);
		_releasing.Add((_textures[slot], lastReadingFence));
		_textures[slot] = new StoredTexture { Released = true };
	}

	/// <summary>Free the resource of every released texture whose last reading fence the frame fence has passed (<paramref name="completedFence"/>).</summary>
	public void CollectReleased(ulong completedFence)
	{
		for (var i = _releasing.Count - 1; i >= 0; i--)
		{
			if (_releasing[i].LastReadingFence > completedFence)
				continue;
			Destroy(_releasing[i].Texture);
			_releasing.RemoveAt(i);
		}
	}

	private static void Destroy(StoredTexture texture)
	{
		if (texture.Resource.Handle is not null)
			texture.Resource.Dispose();
		if (texture.Staging.Handle is not null)
			texture.Staging.Dispose();
	}

	private ComPtr<ID3D12Resource> CreateUploadBuffer(ulong size)
	{
		var heapProps = new HeapProperties { Type = HeapType.Upload, CPUPageProperty = CpuPageProperty.Unknown, MemoryPoolPreference = MemoryPool.Unknown, CreationNodeMask = 1, VisibleNodeMask = 1 };
		var desc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = size,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = DxgiFormat.FormatUnknown,
			SampleDesc = new Silk.NET.DXGI.SampleDesc { Count = 1, Quality = 0 },
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None,
		};
		ComPtr<ID3D12Resource> buffer = default;
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommittedResource(
			&heapProps, HeapFlags.None, &desc, ResourceStates.GenericRead, (ClearValue*)null, out buffer));
		return buffer;
	}

	public void Dispose()
	{
		foreach (var texture in _textures)
			Destroy(texture);
		_textures.Clear();
		foreach (var (texture, _) in _releasing)
			Destroy(texture);
		_releasing.Clear();
	}
}
