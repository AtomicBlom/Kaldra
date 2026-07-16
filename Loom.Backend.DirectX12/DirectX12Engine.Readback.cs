using Loom.Graph;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using DxgiFormat = Silk.NET.DXGI.Format;
using Format = Loom.Core.Graph.Format;

namespace Loom.Backend.DirectX12;

public sealed unsafe partial class DirectX12Engine
{
	/// <summary>
	/// Record (into the open command list) the copy of the just-rendered backbuffer into the readback buffer,
	/// leaving the backbuffer in PRESENT state. Returns the row pitch of the readback layout.
	/// </summary>
	private uint RecordCapture(ComPtr<ID3D12Resource> backbuffer)
	{
		Transition(backbuffer, ResourceStates.RenderTarget, ResourceStates.CopySource);
		var rowPitch = CopyBackbufferToReadback(backbuffer);
		Transition(backbuffer, ResourceStates.CopySource, ResourceStates.Present);
		return rowPitch;
	}

	/// <summary>Record the copy of <paramref name="backbuffer"/> (already in COPY_SOURCE) into the readback buffer; returns the row pitch.</summary>
	private uint CopyBackbufferToReadback(ComPtr<ID3D12Resource> backbuffer)
	{
		var width = _presentTarget.Width;
		var height = _presentTarget.Height;
		var rowPitch = Align(width * 4, 256); // D3D12_TEXTURE_DATA_PITCH_ALIGNMENT
		EnsureReadback((ulong)rowPitch * height);

		var dst = new TextureCopyLocation
		{
			PResource = _readback.Handle,
			Type = TextureCopyType.PlacedFootprint,
			Anonymous = new TextureCopyLocationUnion
			{
				PlacedFootprint = new PlacedSubresourceFootprint
				{
					Offset = 0,
					Footprint = new SubresourceFootprint { Format = _presentTarget.Format, Width = width, Height = height, Depth = 1, RowPitch = rowPitch }
				}
			}
		};
		var src = new TextureCopyLocation
		{
			PResource = backbuffer.Handle,
			Type = TextureCopyType.SubresourceIndex,
			Anonymous = new TextureCopyLocationUnion { SubresourceIndex = 0 }
		};
		_commandList.CopyTextureRegion(&dst, 0, 0, 0, &src, (Box*)null);
		return rowPitch;
	}

	private void EnsureReadback(ulong size)
	{
		if (_readback.Handle is not null && _readbackSize >= size)
			return;
		if (_readback.Handle is not null)
			_readback.Dispose();

		var heapProps = new HeapProperties
		{
			Type = HeapType.Readback,
			CPUPageProperty = CpuPageProperty.Unknown,
			MemoryPoolPreference = MemoryPool.Unknown,
			CreationNodeMask = 1,
			VisibleNodeMask = 1
		};
		var desc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = size,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Silk.NET.DXGI.Format.FormatUnknown,
			SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None
		};
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommittedResource(
			&heapProps, HeapFlags.None, &desc, ResourceStates.CopyDest, (ClearValue*)null, out _readback));
		_readbackSize = size;
	}

	private void WriteCapture(string path, uint rowPitch)
	{
		var width = (int)_presentTarget.Width;
		var height = (int)_presentTarget.Height;

		void* mapped;
		SilkMarshal.ThrowHResult(_readback.Map(0, (Silk.NET.Direct3D12.Range*)null, &mapped));
		var rgba = new byte[width * height * 4];
		var src = (byte*)mapped;
		for (var y = 0; y < height; y++)
			new ReadOnlySpan<byte>(src + (long)y * rowPitch, width * 4).CopyTo(rgba.AsSpan(y * width * 4));
		_readback.Unmap(0, (Silk.NET.Direct3D12.Range*)null);

		PngWriter.WriteRgba(path, width, height, rgba);
		Console.WriteLine($"[loom] DirectX12 captured {width}x{height} -> {path}");
	}

	// Record the copy of each readback target into the slot's READBACK buffer (after the passes that wrote + read
	// it). Transition the target to COPY_SOURCE and track the new state, so next frame's first use of it transitions
	// from the right state (D3D12 needs the exact current state). No-op when the graph has no readback target.
	private void RecordReadback(uint index)
	{
		foreach (var readback in _readbacks)
		{
			var current = _transients!.State(index, readback.Id);
			if (current != ResourceStates.CopySource)
			{
				Transition(_transients.Resource(index, readback.Id), current, ResourceStates.CopySource);
				_transients.SetState(index, readback.Id, ResourceStates.CopySource);
			}

			var dst = new TextureCopyLocation
			{
				PResource = readback.Readback[index].Handle,
				Type = TextureCopyType.PlacedFootprint,
				Anonymous = new TextureCopyLocationUnion
				{
					PlacedFootprint = new PlacedSubresourceFootprint
					{
						Offset = 0,
						Footprint = new SubresourceFootprint { Format = _transients.Format(index, readback.Id), Width = readback.Width, Height = readback.Height, Depth = 1, RowPitch = readback.RowPitch }
					}
				}
			};
			var src = new TextureCopyLocation
			{
				PResource = _transients.Resource(index, readback.Id).Handle,
				Type = TextureCopyType.SubresourceIndex,
				Anonymous = new TextureCopyLocationUnion { SubresourceIndex = 0 }
			};
			_commandList.CopyTextureRegion(&dst, 0, 0, 0, &src, (Box*)null);
			readback.Valid[index] = true;
		}
	}

	// Repack the slot's readback buffers tight (this frame's wait guaranteed their copies finished) and hand them to
	// StoreReadback — D3D12 pads each row to 256 bytes, so copy row by row into the packed scratch. The app decodes
	// whatever format it declared. No-op until a slot's first copy.
	private void DownloadReadback(uint index)
	{
		foreach (var readback in _readbacks)
		{
			if (!readback.Valid[index])
				continue;
			var stride = readback.Width * readback.BytesPerTexel; // packed (unpadded) row stride
			void* mapped;
			SilkMarshal.ThrowHResult(readback.Readback[index].Map(0, (Silk.NET.Direct3D12.Range*)null, &mapped));
			var src = (byte*)mapped;
			for (uint y = 0; y < readback.Height; y++)
			{
				var row = new ReadOnlySpan<byte>(src + (long)y * readback.RowPitch, (int)stride);
				row.CopyTo(new Span<byte>(readback.Packed, (int)(y * stride), (int)stride));
			}
			readback.Readback[index].Unmap(0, (Silk.NET.Direct3D12.Range*)null);
			StoreReadback(readback.Id, readback.Packed);
		}
	}

	// Read this slot's resolved timestamps and decode them into _timings + the frame total. Called after the slot's
	// fence, so the resolve is complete. The readback buffer is tightly-packed UINT64 (no row pitch); the timestamp
	// frequency is ticks per second, so 1000/frequency is the milliseconds-per-tick the decoder needs.
	private void DownloadTiming(uint index)
	{
		if (!GpuTimingEnabled || _timingHeap.Handle is null || _timingPassCount == 0 || !_timingValid[index])
			return;

		void* mapped;
		SilkMarshal.ThrowHResult(_timingReadback[index].Map(0, (Silk.NET.Direct3D12.Range*)null, &mapped));
		var t = new ReadOnlySpan<ulong>(mapped, 2 * _timingPassCount);
		DecodeTimings(t, Barriers, 1000.0 / _timestampFrequency);
		_timingReadback[index].Unmap(0, (Silk.NET.Direct3D12.Range*)null);
	}

	private ComPtr<ID3D12Resource> CreateReadbackBuffer(ulong size)
	{
		var heapProps = new HeapProperties { Type = HeapType.Readback, CPUPageProperty = CpuPageProperty.Unknown, MemoryPoolPreference = MemoryPool.Unknown, CreationNodeMask = 1, VisibleNodeMask = 1 };
		var desc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = size,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = DxgiFormat.FormatUnknown,
			SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None
		};
		ComPtr<ID3D12Resource> buffer = default;
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommittedResource(
			&heapProps, HeapFlags.None, &desc, ResourceStates.CopyDest, (ClearValue*)null, out buffer));
		return buffer;
	}

	private static uint BytesPerTexel(Format format) => format switch
	{
		Format.R16F => 2,
		Format.R32Uint => 4,
		_ => throw new NotSupportedException($"DirectX12: readback of format {format} is not supported (the id buffer is R16F)."),
	};

	private static uint Align(uint value, uint alignment) => (value + alignment - 1) & ~(alignment - 1);
}
