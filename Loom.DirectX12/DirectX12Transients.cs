using Loom.Core;
using Loom.Core.Graph;
using Loom.Graph;

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

using DxgiFormat = Silk.NET.DXGI.Format;

namespace Loom.DirectX12;

/// <summary>
/// The concrete GPU resources backing a graph's transients — the DirectX12 counterpart of the Vulkan backend's
/// <c>TransientResources</c>. Each colour/depth transient gets one texture (plus an RTV/DSV) per
/// frame-in-flight slot, and each buffer one UAV buffer per slot, so a frame's write never races a prior
/// frame's read. The backbuffer is <em>not</em> here (it's the swapchain's facade, handled by the engine).
/// <para>
/// <b>Transient aliasing (§8, §12 M4).</b> The Execute layer's <see cref="AliasPlan"/> partitions the colour/
/// depth transients into buckets whose members are never live at once; this backend turns each bucket (per
/// slot) into <em>one</em> placed-resource <c>ID3D12Heap</c> and creates every member as a
/// <c>CreatePlacedResource</c> at offset 0 — so e.g. the bloom mips reuse the G-buffer's memory once lighting
/// has consumed it. The hand-off needs an <em>aliasing barrier</em> before a member is reactivated: that is
/// the executor's job (it tracks which ids are aliased members and emits the barrier on first use each frame).
/// The heap takes the max member's size/alignment; RT and DS textures share one heap (same heap category, so
/// heap-tier 1 is fine). Buffers and the backbuffer aren't aliased — buffers get their own committed allocation.
/// </para>
/// </summary>
internal sealed unsafe class DirectX12Transients : IDisposable
{
	public struct Target
	{
		public ComPtr<ID3D12Resource> Resource;
		public CpuDescriptorHandle View; // RTV for a colour target, DSV for a depth target
		public DxgiFormat Format;        // the RTV/DSV (and PSO) format — D32_FLOAT for depth, the typed colour format otherwise
		public DxgiFormat SrvFormat;     // the format to sample it through — R32_FLOAT for depth, == Format for colour
		public ResourceStates State;
		public bool IsDepth;
	}

	private readonly Target[][] _targets; // [slot][resourceId]
	private readonly (uint W, uint H)[] _extents; // by resourceId (same across slots)
	private readonly uint[] _byteSizes; // by resourceId, for Buffer-kind resources
	private readonly bool[] _isTransient; // by resourceId
										  // Persistent resources (e.g. the auto-exposure 1×1 image) live ACROSS frames — allocated ONCE, not per-slot,
										  // and never aliased. Stored in slot 0; the accessors redirect persistent ids there so state is shared (a
										  // transition this frame must see where last frame left it), exactly as the Vulkan backend shares one image.
	private readonly bool[] _isPersistent; // by resourceId
	private readonly HashSet<int> _aliasedMemberIds = []; // ids in a multi-member bucket — need an aliasing barrier
	private readonly List<ComPtr<ID3D12Heap>> _heaps = []; // per-bucket-per-slot placed-resource heaps
	private ComPtr<ID3D12DescriptorHeap> _rtvHeap;
	private ComPtr<ID3D12DescriptorHeap> _dsvHeap;

	public DirectX12Transients(DirectX12Context ctx, RenderGraph graph, AliasPlan aliasPlan, uint bufferCount, uint swapWidth, uint swapHeight)
	{
		var count = graph.Resources.Length;
		_isTransient = new bool[count];
		_isPersistent = new bool[count];
		_extents = new (uint, uint)[count];
		_byteSizes = new uint[count];

		var aliasable = new List<ResourceHandle>(); // colour + depth targets (the AliasPlan's domain)
		var bufferTargets = new List<ResourceHandle>();
		var persistentTargets = new List<ResourceHandle>();
		var colorCount = 0;
		var depthCount = 0;
		foreach (var resource in graph.Resources)
		{
			if (resource.Kind == ResourceKind.Backbuffer)
				continue; // the swapchain facade, owned by the engine
			if (resource.Kind is not (ResourceKind.Color or ResourceKind.Depth or ResourceKind.Buffer or ResourceKind.Persistent))
				throw new NotSupportedException($"DirectX12: resource kind {resource.Kind} ('{resource.Name}') is not supported yet.");
			_isTransient[resource.Id] = true; // managed + sampleable (true for persistent too, so it can be sampled)
			if (resource.Kind == ResourceKind.Persistent)
			{
				// Cross-frame storage (auto-exposure): one R16F UAV texture, allocated once, sampled + read-written.
				_isPersistent[resource.Id] = true;
				_extents[resource.Id] = Size(resource, swapWidth, swapHeight);
				persistentTargets.Add(resource);
			}
			else if (resource.Kind == ResourceKind.Buffer)
			{
				_byteSizes[resource.Id] = (uint)resource.ByteSize;
				bufferTargets.Add(resource);
			}
			else
			{
				_extents[resource.Id] = Size(resource, swapWidth, swapHeight);
				aliasable.Add(resource);
				if (resource.Kind == ResourceKind.Depth) depthCount++; else colorCount++;
			}
		}

		_targets = new Target[bufferCount][];
		for (uint slot = 0; slot < bufferCount; slot++)
			_targets[slot] = new Target[count];

		// One RTV per colour target per slot, one DSV per depth target per slot (descriptor heaps for the views —
		// distinct from the placed-resource heaps that back the textures' memory).
		var rtvStart = default(CpuDescriptorHandle);
		var dsvStart = default(CpuDescriptorHandle);
		var rtvInc = ctx.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Rtv);
		var dsvInc = ctx.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Dsv);
		if (colorCount > 0)
		{
			var d = new DescriptorHeapDesc { Type = DescriptorHeapType.Rtv, NumDescriptors = (uint)(bufferCount * colorCount), Flags = DescriptorHeapFlags.None, NodeMask = 0 };
			SilkMarshal.ThrowHResult(ctx.Device.CreateDescriptorHeap(&d, out _rtvHeap));
			rtvStart = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
		}
		if (depthCount > 0)
		{
			var d = new DescriptorHeapDesc { Type = DescriptorHeapType.Dsv, NumDescriptors = (uint)(bufferCount * depthCount), Flags = DescriptorHeapFlags.None, NodeMask = 0 };
			SilkMarshal.ThrowHResult(ctx.Device.CreateDescriptorHeap(&d, out _dsvHeap));
			dsvStart = _dsvHeap.GetCPUDescriptorHandleForHeapStart();
		}

		uint rtvCursor = 0, dsvCursor = 0;
		for (uint slot = 0; slot < bufferCount; slot++)
			AllocateAliasedTargets(ctx, aliasPlan, aliasable, slot, rtvStart, dsvStart, rtvInc, dsvInc, ref rtvCursor, ref dsvCursor);

		AllocateBuffers(ctx, bufferTargets, bufferCount);
		AllocatePersistent(ctx, persistentTargets);

		// A resource that shares a bucket with at least one other resource reuses memory mid-frame, so the
		// executor must aliasing-barrier it before each (re)activation. Singletons need no barrier.
		foreach (var bucket in aliasPlan.Buckets)
			if (bucket.Members.Length > 1)
				foreach (var member in bucket.Members)
					_aliasedMemberIds.Add(member.Id);
	}

	// Group the aliasable (colour/depth) resources by their AliasPlan bucket, back each bucket with one placed-
	// resource heap (sized to its largest member), and create every member at offset 0 so they share the memory.
	private void AllocateAliasedTargets(DirectX12Context ctx, AliasPlan aliasPlan, List<ResourceHandle> aliasable, uint slot,
		CpuDescriptorHandle rtvStart, CpuDescriptorHandle dsvStart, uint rtvInc, uint dsvInc, ref uint rtvCursor, ref uint dsvCursor)
	{
		var groups = new Dictionary<int, List<ResourceHandle>>();
		var next = -1; // synthetic keys for any resource the plan didn't bucket (declared-but-unused)
		foreach (var resource in aliasable)
		{
			var key = aliasPlan.BucketOf(resource) ?? next--;
			(groups.TryGetValue(key, out var list) ? list : groups[key] = []).Add(resource);
		}

		foreach (var members in groups.Values)
		{
			// Size one heap to fit the largest member (all members placed at offset 0 → they alias).
			ulong maxSize = 0, maxAlign = 0;
			foreach (var resource in members)
			{
				var desc = BuildDesc(resource);
				var info = ctx.Device.GetResourceAllocationInfo(0, 1, &desc);
				maxSize = Math.Max(maxSize, info.SizeInBytes);
				maxAlign = Math.Max(maxAlign, info.Alignment);
			}

			var heapDesc = new HeapDesc
			{
				SizeInBytes = maxSize,
				Properties = new HeapProperties { Type = HeapType.Default, CPUPageProperty = CpuPageProperty.Unknown, MemoryPoolPreference = MemoryPool.Unknown, CreationNodeMask = 1, VisibleNodeMask = 1 },
				Alignment = maxAlign,
				Flags = HeapFlags.AllowOnlyRTDSTextures, // colour (RT) + depth (DS) are the same heap category
			};
			ComPtr<ID3D12Heap> heap = default;
			SilkMarshal.ThrowHResult(ctx.Device.CreateHeap(&heapDesc, out heap));
			_heaps.Add(heap);

			foreach (var resource in members)
			{
				var desc = BuildDesc(resource);
				var isDepth = resource.Kind == ResourceKind.Depth;
				// A depth target is created TYPELESS (so it can also be sampled); its DSV/clear use the typed depth
				// format (D32_FLOAT) and its SRV the typed colour-compatible format (R32_FLOAT). Colour: all the same.
				var (_, viewFormat, srvFormat) = DirectX12FormatMapping.ToDxgiViews(resource.Format);
				var clear = BuildClear(viewFormat, isDepth);
				ComPtr<ID3D12Resource> texture = default;
				SilkMarshal.ThrowHResult(ctx.Device.CreatePlacedResource(heap, 0, &desc, ResourceStates.Common, &clear, out texture));
				ctx.SetObjectName((ID3D12Object*)texture.Handle, $"{resource.Name}#{slot}");

				CpuDescriptorHandle view;
				if (isDepth)
				{
					view = new CpuDescriptorHandle { Ptr = dsvStart.Ptr + (nuint)(dsvCursor++ * dsvInc) };
					// A typeless resource can't infer its DSV format — pass an explicit typed (D32_FLOAT) DSV desc.
					var dsvDesc = new DepthStencilViewDesc { Format = viewFormat, ViewDimension = DsvDimension.Texture2D };
					ctx.Device.CreateDepthStencilView(texture, &dsvDesc, view);
				}
				else
				{
					view = new CpuDescriptorHandle { Ptr = rtvStart.Ptr + (nuint)(rtvCursor++ * rtvInc) };
					ctx.Device.CreateRenderTargetView(texture, (RenderTargetViewDesc*)null, view);
				}
				_targets[slot][resource.Id] = new Target { Resource = texture, View = view, Format = viewFormat, SrvFormat = srvFormat, State = ResourceStates.Common, IsDepth = isDepth };
			}
		}
	}

	// One device-local UAV buffer per Buffer resource per frame slot. Buffers aren't aliased (not RT/DS), so
	// they keep a committed allocation; a compute binds it as a root UAV, a draw as a vertex buffer (§7.6).
	private void AllocateBuffers(DirectX12Context ctx, List<ResourceHandle> bufferTargets, uint bufferCount)
	{
		for (uint slot = 0; slot < bufferCount; slot++)
			foreach (var resource in bufferTargets)
			{
				var buffer = CreateBufferResource(ctx, _byteSizes[resource.Id]);
				ctx.SetObjectName((ID3D12Object*)buffer.Handle, $"{resource.Name}#{slot}");
				_targets[slot][resource.Id] = new Target { Resource = buffer, View = default, Format = DxgiFormat.FormatUnknown, SrvFormat = DxgiFormat.FormatUnknown, State = ResourceStates.Common, IsDepth = false };
			}
	}

	// Persistent resources (auto-exposure) carry state across frames, so each is ONE texture shared by every frame
	// slot — NOT multi-buffered like a transient. Stored only in slot 0; the accessors redirect persistent ids there.
	// Created with AllowUnorderedAccess (a compute read-writes it as a storage image) and sampled later as an SRV;
	// R16F is directly UAV- and SRV-creatable, so no typeless split is needed (unlike depth). Uninitialised on the
	// first frame by design — the auto-exposure shader clamps the garbage into a sane range before easing from it.
	private void AllocatePersistent(DirectX12Context ctx, List<ResourceHandle> persistentTargets)
	{
		foreach (var resource in persistentTargets)
		{
			var (w, h) = _extents[resource.Id];
			var (resFormat, _, srvFormat) = DirectX12FormatMapping.ToDxgiViews(resource.Format);
			var heapProps = new HeapProperties { Type = HeapType.Default, CPUPageProperty = CpuPageProperty.Unknown, MemoryPoolPreference = MemoryPool.Unknown, CreationNodeMask = 1, VisibleNodeMask = 1 };
			var desc = new ResourceDesc
			{
				Dimension = ResourceDimension.Texture2D,
				Alignment = 0,
				Width = w,
				Height = h,
				DepthOrArraySize = 1,
				MipLevels = 1,
				Format = resFormat,
				SampleDesc = new Silk.NET.DXGI.SampleDesc { Count = 1, Quality = 0 },
				Layout = TextureLayout.LayoutUnknown,
				Flags = ResourceFlags.AllowUnorderedAccess,
			};
			ComPtr<ID3D12Resource> texture = default;
			SilkMarshal.ThrowHResult(ctx.Device.CreateCommittedResource(
				&heapProps, HeapFlags.None, &desc, ResourceStates.Common, (ClearValue*)null, out texture));
			ctx.SetObjectName((ID3D12Object*)texture.Handle, $"{resource.Name}#persistent");
			_targets[0][resource.Id] = new Target { Resource = texture, View = default, Format = resFormat, SrvFormat = srvFormat, State = ResourceStates.Common, IsDepth = false };
		}
	}

	// Persistent resources live in slot 0 only (one shared instance); redirect their lookups there.
	private uint SlotOf(uint slot, int id) => _isPersistent[id] ? 0u : slot;

	public bool IsTransient(int id) => _isTransient[id];
	public bool IsAliasedMember(int id) => _aliasedMemberIds.Contains(id);
	public (uint W, uint H) Extent(int id) => _extents[id];
	public uint ByteSize(int id) => _byteSizes[id];
	public DxgiFormat Format(uint slot, int id) => _targets[SlotOf(slot, id)][id].Format;
	public DxgiFormat SrvFormat(uint slot, int id) => _targets[SlotOf(slot, id)][id].SrvFormat;
	public CpuDescriptorHandle View(uint slot, int id) => _targets[SlotOf(slot, id)][id].View;
	public ComPtr<ID3D12Resource> Resource(uint slot, int id) => _targets[SlotOf(slot, id)][id].Resource;
	public ResourceStates State(uint slot, int id) => _targets[SlotOf(slot, id)][id].State;
	public void SetState(uint slot, int id, ResourceStates state) => _targets[SlotOf(slot, id)][id].State = state;

	private ResourceDesc BuildDesc(ResourceHandle resource)
	{
		var (w, h) = _extents[resource.Id];
		// Depth is created TYPELESS (R32_TYPELESS) so the same resource can carry a typed DSV and a typed SRV (a
		// D32_FLOAT resource can't be sampled); colour uses its typed format directly.
		var (format, _, _) = DirectX12FormatMapping.ToDxgiViews(resource.Format);
		var flags = resource.Kind == ResourceKind.Depth ? ResourceFlags.AllowDepthStencil : ResourceFlags.AllowRenderTarget;
		return new ResourceDesc
		{
			Dimension = ResourceDimension.Texture2D,
			Alignment = 0,
			Width = w,
			Height = h,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = format,
			SampleDesc = new Silk.NET.DXGI.SampleDesc { Count = 1, Quality = 0 },
			Layout = TextureLayout.LayoutUnknown,
			Flags = flags,
		};
	}

	// Optimized clear value matching the executor's clear (black colour / depth 1.0), so the debug layer
	// doesn't warn on ClearRenderTargetView / ClearDepthStencilView.
	private static ClearValue BuildClear(DxgiFormat format, bool isDepth)
	{
		var clear = new ClearValue { Format = format };
		if (isDepth)
			clear.Anonymous.DepthStencil = new DepthStencilValue { Depth = 1f, Stencil = 0 };
		else
			clear.Anonymous.Color[3] = 1f;
		return clear;
	}

	private static (uint W, uint H) Size(ResourceHandle resource, uint swapWidth, uint swapHeight)
	{
		var domain = resource.Domain;
		if (domain.IsBackbuffer)
			return (Scale(swapWidth, resource.Scale), Scale(swapHeight, resource.Scale));
		if (domain.RelativeScale > 0f)
			return (Scale(swapWidth, domain.RelativeScale * resource.Scale), Scale(swapHeight, domain.RelativeScale * resource.Scale));
		var (w, h) = domain.Resolve(resource.Scale);
		return ((uint)Math.Max(1, w), (uint)Math.Max(1, h));
	}

	private static uint Scale(uint value, float scale) => (uint)Math.Max(1, (int)MathF.Round(value * scale));

	// A device-local (Default-heap) buffer the compute writes via a root UAV and a draw reads as a vertex
	// stream — created with AllowUnorderedAccess, starting in COMMON (the executor transitions it per the plan).
	private static ComPtr<ID3D12Resource> CreateBufferResource(DirectX12Context ctx, uint byteSize)
	{
		var heapProps = new HeapProperties { Type = HeapType.Default, CPUPageProperty = CpuPageProperty.Unknown, MemoryPoolPreference = MemoryPool.Unknown, CreationNodeMask = 1, VisibleNodeMask = 1 };
		var desc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = byteSize,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = DxgiFormat.FormatUnknown,
			SampleDesc = new Silk.NET.DXGI.SampleDesc { Count = 1, Quality = 0 },
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.AllowUnorderedAccess,
		};
		ComPtr<ID3D12Resource> buffer = default;
		SilkMarshal.ThrowHResult(ctx.Device.CreateCommittedResource(
			&heapProps, HeapFlags.None, &desc, ResourceStates.Common, (ClearValue*)null, out buffer));
		return buffer;
	}

	public void Dispose()
	{
		foreach (var slot in _targets)
			foreach (var target in slot)
				if (target.Resource.Handle is not null)
					target.Resource.Dispose();
		foreach (var heap in _heaps)
			if (heap.Handle is not null)
				heap.Dispose();
		if (_rtvHeap.Handle is not null)
			_rtvHeap.Dispose();
		if (_dsvHeap.Handle is not null)
			_dsvHeap.Dispose();
	}
}
