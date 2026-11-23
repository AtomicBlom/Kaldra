using System.Buffers.Binary;
using System.Numerics;

namespace BinaryVibrance.Kaldra.Backend.WebGPU;

/// <summary>
/// Builds the per-frame binary command stream that <c>kaldra-webgpu.js</c> replays into one WebGPU command
/// encoder — the coarse-grained protocol that keeps the WASM↔JS boundary to a single crossing per frame.
/// Everything is little-endian and 4-byte-word aligned, so the replayer walks it with one Int32Array and one
/// Float32Array over the same memory. Stream layout: <c>[magic 'LWGB'][version][totalWords]</c> then records of
/// <c>[opcode, …fixed payload…]</c>; variable-length lists are count-prefixed. Op semantics the replayer relies
/// on: <see cref="WriteBufferFromArena"/> executes at queue level (before every pass of the frame, matching
/// <c>queue.writeBuffer</c> ordering), so a buffer must receive at most one arena write per frame.
/// </summary>
internal sealed class WebGpuCommandWriter
{
	public const int Magic = 0x4C574742; // 'LWGB'

	public const int OpEnd = 0;
	public const int OpBeginRenderPass = 1;
	public const int OpEndPass = 2;
	public const int OpSetRenderPipeline = 3;
	public const int OpSetBindGroup = 4;
	public const int OpSetVertexBuffer = 5;
	public const int OpSetIndexBuffer = 6;
	public const int OpDraw = 7;
	public const int OpDrawIndexed = 8;
	public const int OpSetViewport = 9;
	public const int OpSetScissor = 10;
	public const int OpBeginComputePass = 11;
	public const int OpSetComputePipeline = 12;
	public const int OpDispatch = 13;
	public const int OpWriteBuffer = 14;
	public const int OpCopyTextureToBuffer = 15;
	public const int OpDrawIndexedIndirect = 16;
	public const int OpResolve = 17;

	// Load/store action encodings (match LOAD_OPS/STORE_OPS in the shim).
	public const int LoadClear = 0;
	public const int LoadLoad = 1;
	public const int LoadDontCare = 2;
	public const int StoreStore = 0;
	public const int StoreDiscard = 1;

	private byte[] _bytes = new byte[16 * 1024];
	private int _offset;

	public void Reset()
	{
		_offset = 0;
		WriteInt(Magic);
		WriteInt(WebGpuInterop.ProtocolVersion);
		WriteInt(0); // total word count, patched by Finish
	}

	/// <summary>Patch the header's word count and return the finished stream.</summary>
	public Span<byte> Finish()
	{
		WriteInt(OpEnd);
		BinaryPrimitives.WriteInt32LittleEndian(_bytes.AsSpan(8), _offset / 4);
		return _bytes.AsSpan(0, _offset);
	}

	/// <summary>Begin a render pass. Call <see cref="ColorAttachment"/> exactly <paramref name="colorCount"/>
	/// times, then <see cref="DepthAttachment"/> if <paramref name="hasDepth"/>.</summary>
	public void BeginRenderPass(int colorCount, bool hasDepth)
	{
		WriteInt(OpBeginRenderPass);
		WriteInt(colorCount);
		WriteInt(hasDepth ? 1 : 0);
	}

	public void ColorAttachment(int viewHandle, int loadOp, int storeOp, Vector4 clear)
	{
		WriteInt(viewHandle);
		WriteInt(loadOp);
		WriteInt(storeOp);
		WriteFloat(clear.X);
		WriteFloat(clear.Y);
		WriteFloat(clear.Z);
		WriteFloat(clear.W);
	}

	public void DepthAttachment(int viewHandle, int loadOp, int storeOp, float clearDepth, bool readOnly)
	{
		WriteInt(viewHandle);
		WriteInt(loadOp);
		WriteInt(storeOp);
		WriteFloat(clearDepth);
		WriteInt(readOnly ? 1 : 0);
	}

	public void EndPass() => WriteInt(OpEndPass);

	/// <summary>Resolve a multisampled view into a single-sample one, between passes: a pass that loads it, draws nothing,
	/// and resolves it, which is the only resolve WebGPU has.</summary>
	public void Resolve(int sourceView, int targetView)
	{
		WriteInt(OpResolve);
		WriteInt(sourceView);
		WriteInt(targetView);
	}

	public void SetRenderPipeline(int pipelineHandle)
	{
		WriteInt(OpSetRenderPipeline);
		WriteInt(pipelineHandle);
	}

	public void SetBindGroup(int groupIndex, int bindGroupHandle, ReadOnlySpan<int> dynamicOffsets)
	{
		WriteInt(OpSetBindGroup);
		WriteInt(groupIndex);
		WriteInt(bindGroupHandle);
		WriteInt(dynamicOffsets.Length);
		foreach (var offset in dynamicOffsets)
			WriteInt(offset);
	}

	public void SetVertexBuffer(int slot, int bufferHandle, int byteOffset)
	{
		WriteInt(OpSetVertexBuffer);
		WriteInt(slot);
		WriteInt(bufferHandle);
		WriteInt(byteOffset);
	}

	/// <summary><paramref name="indexFormat"/>: 0 = uint16, 1 = uint32.</summary>
	public void SetIndexBuffer(int bufferHandle, int indexFormat, int byteOffset)
	{
		WriteInt(OpSetIndexBuffer);
		WriteInt(bufferHandle);
		WriteInt(indexFormat);
		WriteInt(byteOffset);
	}

	public void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance)
	{
		WriteInt(OpDraw);
		WriteInt(vertexCount);
		WriteInt(instanceCount);
		WriteInt(firstVertex);
		WriteInt(firstInstance);
	}

	public void DrawIndexed(int indexCount, int instanceCount, int firstIndex, int baseVertex, int firstInstance)
	{
		WriteInt(OpDrawIndexed);
		WriteInt(indexCount);
		WriteInt(instanceCount);
		WriteInt(firstIndex);
		WriteInt(baseVertex);
		WriteInt(firstInstance);
	}

	/// <summary>Draw with the counts taken from a buffer the GPU filled — WebGPU's drawIndexedIndirect.</summary>
	public void DrawIndexedIndirect(int bufferHandle, int byteOffset)
	{
		WriteInt(OpDrawIndexedIndirect);
		WriteInt(bufferHandle);
		WriteInt(byteOffset);
	}

	public void SetViewport(float x, float y, float width, float height)
	{
		WriteInt(OpSetViewport);
		WriteFloat(x);
		WriteFloat(y);
		WriteFloat(width);
		WriteFloat(height);
	}

	public void SetScissor(int x, int y, int width, int height)
	{
		WriteInt(OpSetScissor);
		WriteInt(x);
		WriteInt(y);
		WriteInt(width);
		WriteInt(height);
	}

	public void BeginComputePass() => WriteInt(OpBeginComputePass);

	public void SetComputePipeline(int pipelineHandle)
	{
		WriteInt(OpSetComputePipeline);
		WriteInt(pipelineHandle);
	}

	public void Dispatch(int x, int y, int z)
	{
		WriteInt(OpDispatch);
		WriteInt(x);
		WriteInt(y);
		WriteInt(z);
	}

	/// <summary>Replayed as <c>queue.writeBuffer(buffer, dstOffset, arena, arenaOffset, byteSize)</c> — lands
	/// before every pass of the frame regardless of stream position.</summary>
	public void WriteBufferFromArena(int bufferHandle, int dstOffset, int arenaOffset, int byteSize)
	{
		WriteInt(OpWriteBuffer);
		WriteInt(bufferHandle);
		WriteInt(dstOffset);
		WriteInt(arenaOffset);
		WriteInt(byteSize);
	}

	/// <summary><paramref name="bytesPerRow"/> must be 256-byte aligned (WebGPU copy requirement).</summary>
	public void CopyTextureToBuffer(int textureHandle, int width, int height, int bufferHandle, int bufferOffset, int bytesPerRow)
	{
		WriteInt(OpCopyTextureToBuffer);
		WriteInt(textureHandle);
		WriteInt(width);
		WriteInt(height);
		WriteInt(bufferHandle);
		WriteInt(bufferOffset);
		WriteInt(bytesPerRow);
	}

	private void WriteInt(int value)
	{
		EnsureCapacity(4);
		BinaryPrimitives.WriteInt32LittleEndian(_bytes.AsSpan(_offset), value);
		_offset += 4;
	}

	private void WriteFloat(float value)
	{
		EnsureCapacity(4);
		BinaryPrimitives.WriteSingleLittleEndian(_bytes.AsSpan(_offset), value);
		_offset += 4;
	}

	private void EnsureCapacity(int bytes)
	{
		if (_offset + bytes <= _bytes.Length)
			return;
		var grown = new byte[Math.Max(_bytes.Length * 2, _offset + bytes)];
		_bytes.AsSpan(0, _offset).CopyTo(grown);
		_bytes = grown;
	}
}
