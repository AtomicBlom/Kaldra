using System.Runtime.Versioning;

using Loom.Core.Graph;
using Loom.Graph;

namespace Loom.Backend.WebGPU;

/// <summary>
/// The GPU textures and buffers backing a graph's resources. Unlike the native backends there is no
/// per-frame-slot multi-buffering: successive <c>queue.submit</c> calls execute in order and WebGPU inserts
/// the cross-submit synchronisation itself, so one physical texture/buffer per resource is race-free. The
/// Execute layer's <see cref="AliasPlan"/> is ignored — WebGPU exposes no memory aliasing, so every transient
/// is allocated independently (the plan's only cost here is peak VRAM, deliberately traded for portability).
/// <para>
/// WebGPU requires every usage a resource will ever have declared at creation, so each texture/buffer's usage
/// mask is the union of all states it takes across the whole <see cref="BarrierPlan"/> (see
/// <see cref="WebGpuStateMapping"/>). The backbuffer is not owned here: it resolves to the sentinel view handle
/// (<see cref="WebGpuHandleAllocator.BackbufferView"/>), which the JS shim maps to the canvas's current texture.
/// </para>
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class WebGpuTransientResources : IDisposable
{
	internal struct ResourceTexture
	{
		public int ViewHandle;   // BackbufferView sentinel for the swapchain target
		public int TextureHandle; // -1 for the backbuffer (JS owns it)
		public Format Format;
		public int Width;
		public int Height;
		public bool IsBackbuffer;
		public bool SwapchainSized;
		public int Usage;
	}

	private readonly WebGpuHandleAllocator _handles;
	private readonly RenderGraph _graph;
	private readonly ResourceTexture[] _textures; // indexed by ResourceHandle.Id (-1 handles for non-image kinds)
	private readonly int[] _buffers;              // indexed by ResourceHandle.Id, -1 for non-buffer kinds
	private int _swapWidth;
	private int _swapHeight;

	public WebGpuTransientResources(WebGpuHandleAllocator handles, RenderGraph graph, BarrierPlan barriers, int swapWidth, int swapHeight)
	{
		_handles = handles;
		_graph = graph;
		_swapWidth = swapWidth;
		_swapHeight = swapHeight;
		_textures = new ResourceTexture[graph.Resources.Length];
		_buffers = new int[graph.Resources.Length];
		Array.Fill(_buffers, -1);

		var readback = graph.ReadbackTargets.Select(r => r.Id).ToHashSet();
		foreach (var resource in graph.Resources)
			InitResource(resource, barriers, readback.Contains(resource.Id));
	}

	private void InitResource(ResourceHandle resource, BarrierPlan barriers, bool isReadback)
	{
		ref var tex = ref _textures[resource.Id];
		tex.TextureHandle = -1;
		tex.ViewHandle = -1;

		switch (resource.Kind)
		{
			case ResourceKind.Backbuffer:
				tex.IsBackbuffer = true;
				tex.SwapchainSized = true;
				tex.ViewHandle = WebGpuHandleAllocator.BackbufferView;
				tex.Format = resource.Format;
				(tex.Width, tex.Height) = (_swapWidth, _swapHeight);
				return;

			case ResourceKind.Color or ResourceKind.Depth or ResourceKind.Persistent:
				tex.Format = resource.Format;
				tex.SwapchainSized = resource.Domain.IsBackbuffer || resource.Domain.RelativeScale > 0f;
				tex.Usage = WebGpuStateMapping.UnionTextureUsage(barriers, resource.Id, copySource: isReadback);
				CreateTexture(resource, ref tex);
				return;

			case ResourceKind.Constants:
				_buffers[resource.Id] = CreateBuffer(resource.ByteSize,
					WebGpuStateMapping.BufferUniform | WebGpuStateMapping.BufferCopyDst, resource.Name);
				return;

			case ResourceKind.Storage:
				_buffers[resource.Id] = CreateBuffer(resource.ByteSize,
					WebGpuStateMapping.BufferStorage | WebGpuStateMapping.BufferCopyDst, resource.Name);
				return;

			case ResourceKind.Buffer:
				// GPU-written storage that is also drawn as vertices (the particle path).
				_buffers[resource.Id] = CreateBuffer(resource.ByteSize,
					WebGpuStateMapping.BufferStorage | WebGpuStateMapping.BufferVertex | WebGpuStateMapping.BufferCopyDst, resource.Name);
				return;
		}
	}

	private void CreateTexture(ResourceHandle resource, ref ResourceTexture tex)
	{
		var (w, h) = resource.Resolve(_swapWidth, _swapHeight);
		tex.Width = w;
		tex.Height = h;
		tex.TextureHandle = _handles.Allocate(WebGpuHandleAllocator.Textures);
		var formatId = WebGpuFormatMapping.ToWebGpu(resource.Format);
		WebGpuInterop.CreateTexture(tex.TextureHandle, w, h, mipCount: 1, formatId, tex.Usage, sampleCount: 1, resource.Name);
		tex.ViewHandle = _handles.Allocate(WebGpuHandleAllocator.Views);
		WebGpuInterop.CreateTextureView(tex.ViewHandle, tex.TextureHandle, formatId, baseMip: 0, mipCount: 1, resource.Name);
	}

	private int CreateBuffer(int byteSize, int usage, string label)
	{
		var handle = _handles.Allocate(WebGpuHandleAllocator.Buffers);
		WebGpuInterop.CreateBuffer(handle, Math.Max(byteSize, 4), usage, label);
		return handle;
	}

	/// <summary>The WebGPU view handle for a colour/depth resource (or the backbuffer sentinel).</summary>
	public int ViewHandle(int id) => _textures[id].ViewHandle;

	/// <summary>The backing texture handle (or -1 for the backbuffer).</summary>
	public int TextureHandle(int id) => _textures[id].TextureHandle;

	public ref ResourceTexture Texture(int id) => ref _textures[id];

	/// <summary>The backing GPU buffer handle for a Constants/Storage/Buffer resource (or -1).</summary>
	public int BufferHandle(int id) => _buffers[id];

	/// <summary>Reallocate a growable storage resource's buffer to <paramref name="newByteSize"/> bytes.</summary>
	public void GrowStorage(int id, int newByteSize)
	{
		var resource = _graph.Resources[id];
		_handles.Release(WebGpuHandleAllocator.Buffers, _buffers[id]);
		_buffers[id] = CreateBuffer(newByteSize,
			WebGpuStateMapping.BufferStorage | WebGpuStateMapping.BufferCopyDst, resource.Name);
	}

	/// <summary>Recreate swapchain-sized textures after a canvas resize; fixed-domain resources are untouched.</summary>
	public void Resize(int swapWidth, int swapHeight)
	{
		_swapWidth = swapWidth;
		_swapHeight = swapHeight;
		foreach (var resource in _graph.Resources)
		{
			ref var tex = ref _textures[resource.Id];
			if (tex.IsBackbuffer)
			{
				(tex.Width, tex.Height) = (swapWidth, swapHeight);
				continue;
			}
			if (!tex.SwapchainSized || tex.TextureHandle < 0)
				continue;
			_handles.Release(WebGpuHandleAllocator.Views, tex.ViewHandle);
			_handles.Release(WebGpuHandleAllocator.Textures, tex.TextureHandle);
			CreateTexture(resource, ref tex);
		}
	}

	public void Dispose()
	{
		foreach (ref var tex in _textures.AsSpan())
		{
			if (tex.ViewHandle >= 0 && !tex.IsBackbuffer)
				_handles.Release(WebGpuHandleAllocator.Views, tex.ViewHandle);
			if (tex.TextureHandle >= 0)
				_handles.Release(WebGpuHandleAllocator.Textures, tex.TextureHandle);
		}
		foreach (var buffer in _buffers)
			if (buffer >= 0)
				_handles.Release(WebGpuHandleAllocator.Buffers, buffer);
	}
}
