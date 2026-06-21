using System.Runtime.InteropServices;
using Loom.Core.Materials;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

using DxgiFormat = Silk.NET.DXGI.Format;

namespace Loom.DirectX12;

/// <summary>
/// Owns GPU-resident textures — the DirectX12 counterpart of the Vulkan backend's <see cref="TextureStore"/>
/// (PlanningPhase2.md M11). Each texture is a DEFAULT-heap committed <c>Texture2D</c> with a full mip chain,
/// uploaded once from an UPLOAD-heap staging buffer via per-subresource <c>CopyTextureRegion</c>, then left in
/// <c>PixelShaderResource</c>. Mip levels arrive pre-built on the CPU in <see cref="TextureData"/>, so the
/// store just lays each level into the staging buffer at the footprint <c>GetCopyableFootprints</c> reports.
/// The upload is synchronous (a private allocator + list + fence), like the Vulkan store's queue-wait — fine
/// for build-once assets.
/// </summary>
internal sealed unsafe class DirectX12TextureStore : IDisposable
{
	private struct StoredTexture
	{
		public ComPtr<ID3D12Resource> Resource;
		public DxgiFormat Format;
		public uint MipLevels;
		public bool IsCube;
	}

	private readonly DirectX12Context _ctx;
	private readonly List<StoredTexture> _textures = [];

	private ComPtr<ID3D12CommandAllocator> _allocator;
	private ComPtr<ID3D12GraphicsCommandList> _cmd;
	private ComPtr<ID3D12Fence> _fence;
	private ulong _fenceValue;
	private readonly nint _fenceEvent;

	public DirectX12TextureStore(DirectX12Context ctx)
	{
		_ctx = ctx;
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommandAllocator(CommandListType.Direct, out _allocator));
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommandList(0, CommandListType.Direct, _allocator, default(ComPtr<ID3D12PipelineState>), out _cmd));
		SilkMarshal.ThrowHResult(_cmd.Close());
		SilkMarshal.ThrowHResult(_ctx.Device.CreateFence(0, FenceFlags.None, out _fence));
		_fenceEvent = CreateEventW(0, 0, 0, 0);
		if (_fenceEvent == 0)
			throw new InvalidOperationException("DirectX12: CreateEvent failed for the texture-upload fence.");
	}

	/// <summary>Upload a texture + its mip chain and return a handle a material can bundle.</summary>
	public Texture Create(TextureData data)
	{
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

		// One-time copy: lay each subresource in, then transition the whole texture to a shader resource.
		SilkMarshal.ThrowHResult(_allocator.Reset());
		SilkMarshal.ThrowHResult(_cmd.Reset(_allocator, default(ComPtr<ID3D12PipelineState>)));
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
			_cmd.CopyTextureRegion(&dst, 0, 0, 0, &src, (Box*)null);
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
		_cmd.ResourceBarrier(1, &barrier);
		SilkMarshal.ThrowHResult(_cmd.Close());

		var list = (ID3D12CommandList*)_cmd.Handle;
		_ctx.GraphicsQueue.ExecuteCommandLists(1, &list);
		WaitForGpu();
		staging.Dispose();

		var id = _textures.Count;
		_textures.Add(new StoredTexture { Resource = texture, Format = format, MipLevels = mipsPerFace, IsCube = data.IsCube });
		return new Texture(id, data.Width, data.Height);
	}

	/// <summary>The texture resource a material binding creates an SRV over.</summary>
	public ComPtr<ID3D12Resource> Resource(int textureId) => _textures[textureId].Resource;

	/// <summary>The DXGI format an SRV over this texture uses.</summary>
	public DxgiFormat SrvFormat(int textureId) => _textures[textureId].Format;

	/// <summary>The texture's mip-level count (so the SRV exposes the whole chain, not just mip 0).</summary>
	public uint MipLevels(int textureId) => _textures[textureId].MipLevels;

	/// <summary>True when the texture is a cube map (six array slices), so the SRV is created as a <c>TextureCube</c>.</summary>
	public bool IsCube(int textureId) => _textures[textureId].IsCube;

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

	private void WaitForGpu()
	{
		_fenceValue++;
		SilkMarshal.ThrowHResult(_ctx.GraphicsQueue.Signal(_fence, _fenceValue));
		if (_fence.GetCompletedValue() < _fenceValue)
		{
			SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_fenceValue, (void*)_fenceEvent));
			WaitForSingleObject(_fenceEvent, 0xFFFFFFFF);
		}
	}

	public void Dispose()
	{
		foreach (var texture in _textures)
			if (texture.Resource.Handle is not null)
				texture.Resource.Dispose();
		_textures.Clear();
		if (_cmd.Handle is not null)
			_cmd.Dispose();
		if (_allocator.Handle is not null)
			_allocator.Dispose();
		if (_fence.Handle is not null)
			_fence.Dispose();
		if (_fenceEvent != 0)
			CloseHandle(_fenceEvent);
	}

	[DllImport("kernel32", SetLastError = true)]
	private static extern nint CreateEventW(nint lpEventAttributes, int bManualReset, int bInitialState, nint lpName);

	[DllImport("kernel32", SetLastError = true)]
	private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

	[DllImport("kernel32", SetLastError = true)]
	private static extern int CloseHandle(nint hObject);
}
