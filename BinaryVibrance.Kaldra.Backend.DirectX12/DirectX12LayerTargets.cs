using BinaryVibrance.Kaldra.Core.Hosting;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using DxgiFormat = Silk.NET.DXGI.Format;

namespace BinaryVibrance.Kaldra.Backend.DirectX12;

/// <summary>
/// The renderer-private offscreen targets backing UI group-opacity layers (the <c>BeginLayer</c>/<c>EndLayer</c>
/// markers a <c>ctx.DrawList</c> submission carries) — the DirectX12 twin of <c>VulkanLayerTargets</c>. These are
/// <em>not</em> graph resources: they live below the graph seam so the render graph still sees one <c>ui</c> pass and
/// its hash never moves. A layer target matches the scope its list draws into, so targets are pooled per (size, colour
/// format) — one frame can layer UI on targets of several sizes, a HUD on the backbuffer and a panel on its own
/// texture — and within a size per (frame slot, nesting depth): nested layers need distinct targets at once, while
/// sibling layers at the same depth reuse one (the render-target↔shader-resource transitions order the prior composite
/// read before the next clear). Pooling at the pass colour format lets the already-baked <c>ui</c> pipeline render
/// into a layer unchanged.
/// <para>
/// Targets are kept across frames and released only in <see cref="BeginFrame"/>, once their frame slot's earlier
/// frame has completed — never while a recorded frame may still use them. A size a slot stops layering at (one a
/// resize left behind) is retired after <see cref="RetireAfterIdleFrames"/> of that slot's frames without it.
/// </para>
/// </summary>
internal sealed unsafe class DirectX12LayerTargets : IDisposable
{
	/// <summary>
	/// How many of its own frames a slot keeps a size's targets without layering at it: long enough that a UI layering
	/// on and off (a fade now and then) keeps them, short enough that sizes a resize leaves behind go within a second.
	/// </summary>
	internal const int RetireAfterIdleFrames = 30;

	// A practical ceiling on UI group nesting; each size's RTV heap is sized for this many targets per frame slot.
	// Sized from the neutral cap the caller enforces, not a number of its own: the pool is indexed by nesting depth, so
	// a pool smaller than the cap is an out-of-range write and a pool larger is memory nothing can reach.
	private const int MaxDepth = DrawListGeometry.MaxLayerNesting;

	private readonly DirectX12Context _ctx;
	private readonly int _slots;
	private readonly uint _rtvInc;

	internal struct Target
	{
		public ComPtr<ID3D12Resource> Resource;
		public CpuDescriptorHandle Rtv;
		public ResourceStates State;
		public bool Created;
	}

	// One size's targets, [slot][depth], with the RTV heap their views live in.
	private sealed class SizeClass
	{
		public readonly uint Width, Height;
		public readonly DxgiFormat Format;
		public ComPtr<ID3D12DescriptorHeap> RtvHeap;
		public readonly Target[][] Pools;
		public readonly bool[] UsedThisFrame;
		public readonly int[] IdleFrames;

		public SizeClass(DirectX12Context ctx, uint width, uint height, DxgiFormat format, int slots)
		{
			Width = width;
			Height = height;
			Format = format;
			var desc = new DescriptorHeapDesc { Type = DescriptorHeapType.Rtv, NumDescriptors = (uint)(slots * MaxDepth), Flags = DescriptorHeapFlags.None, NodeMask = 0 };
			SilkMarshal.ThrowHResult(ctx.Device.CreateDescriptorHeap(&desc, out ComPtr<ID3D12DescriptorHeap> madeRtvHeap));
			RtvHeap = madeRtvHeap;
			Pools = new Target[slots][];
			for (var i = 0; i < slots; i++)
				Pools[i] = new Target[MaxDepth];
			UsedThisFrame = new bool[slots];
			IdleFrames = new int[slots];
		}

		public bool Matches(uint width, uint height, DxgiFormat format) => Width == width && Height == height && Format == format;
	}

	private readonly List<SizeClass> _classes = [];
	private uint _width, _height;
	private DxgiFormat _format;
	private SizeClass? _current; // the class matching _width/_height/_format, found on the first Acquire after Configure

	public DirectX12LayerTargets(DirectX12Context ctx, int frameCount)
	{
		_ctx = ctx;
		_slots = frameCount;
		_rtvInc = ctx.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Rtv);
	}

	/// <summary>The colour format the layer targets are rendered/sampled at (the pass colour format).</summary>
	public DxgiFormat Format => _format;

	/// <summary>
	/// Start <paramref name="slot"/>'s frame. Its previous frame has completed, so nothing still recorded reads the
	/// slot's targets: a size the slot has gone <see cref="RetireAfterIdleFrames"/> frames without layering at has its
	/// targets for the slot released here, and a size no slot holds targets for any more is forgotten.
	/// </summary>
	public void BeginFrame(uint slot)
	{
		for (var i = _classes.Count - 1; i >= 0; i--)
		{
			var size = _classes[i];
			if (size.UsedThisFrame[slot])
			{
				size.UsedThisFrame[slot] = false;
				size.IdleFrames[slot] = 0;
				continue;
			}
			if (++size.IdleFrames[slot] < RetireAfterIdleFrames || !HasTargets(size.Pools[slot]))
				continue;

			ReleaseTargets(size.Pools[slot]);
			if (IsEmpty(size))
			{
				size.RtvHeap.Dispose();
				_classes.RemoveAt(i);
				if (ReferenceEquals(_current, size))
					_current = null;
			}
		}
	}

	/// <summary>
	/// Bind the pool to the size and colour format of the scope about to draw a list. Targets of other sizes are kept
	/// for the lists that draw at those sizes. Free, and allocates nothing: a list that opens no layer never makes a
	/// size's pool.
	/// </summary>
	public void Configure(uint width, uint height, DxgiFormat format)
	{
		if (_width == width && _height == height && _format == format)
			return;
		_width = width;
		_height = height;
		_format = format;
		_current = null;
	}

	/// <summary>Get (creating on first use) the layer target at the configured size for <paramref name="slot"/> at
	/// nesting <paramref name="depth"/>.</summary>
	public ref Target Acquire(uint slot, int depth)
	{
		var size = _current ??= FindOrAddClass();
		size.UsedThisFrame[slot] = true;
		ref var t = ref size.Pools[slot][depth];
		if (!t.Created)
			Create(size, ref t, slot, depth);
		return ref t;
	}

	private SizeClass FindOrAddClass()
	{
		for (var i = 0; i < _classes.Count; i++)
		{
			if (_classes[i].Matches(_width, _height, _format))
				return _classes[i];
		}
		var added = new SizeClass(_ctx, _width, _height, _format, _slots);
		_classes.Add(added);
		return added;
	}

	private void Create(SizeClass size, ref Target t, uint slot, int depth)
	{
		var heapProps = new HeapProperties { Type = HeapType.Default, CPUPageProperty = CpuPageProperty.Unknown, MemoryPoolPreference = MemoryPool.Unknown, CreationNodeMask = 1, VisibleNodeMask = 1 };
		var desc = new ResourceDesc
		{
			Dimension = ResourceDimension.Texture2D,
			Alignment = 0,
			Width = size.Width,
			Height = size.Height,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = size.Format,
			SampleDesc = new Silk.NET.DXGI.SampleDesc { Count = 1, Quality = 0 },
			Layout = TextureLayout.LayoutUnknown,
			Flags = ResourceFlags.AllowRenderTarget,
		};
		// Optimised clear = transparent black (the layer starts empty), matching the ClearRenderTargetView the caller
		// issues, so the debug layer doesn't warn.
		var clear = new ClearValue { Format = size.Format };
		clear.Anonymous.Color[0] = 0f;
		clear.Anonymous.Color[1] = 0f;
		clear.Anonymous.Color[2] = 0f;
		clear.Anonymous.Color[3] = 0f;
		ComPtr<ID3D12Resource> resource = default;
		SilkMarshal.ThrowHResult(_ctx.Device.CreateCommittedResource(
			&heapProps, HeapFlags.None, &desc, ResourceStates.RenderTarget, &clear, out resource));
		_ctx.SetObjectName((ID3D12Object*)resource.Handle, $"ui.layer#{slot}.{depth}@{size.Width}x{size.Height}");

		var rtv = new CpuDescriptorHandle { Ptr = size.RtvHeap.GetCPUDescriptorHandleForHeapStart().Ptr + (nuint)((slot * MaxDepth + (uint)depth) * _rtvInc) };
		_ctx.Device.CreateRenderTargetView(resource, (RenderTargetViewDesc*)null, rtv);

		t.Resource = resource;
		t.Rtv = rtv;
		t.State = ResourceStates.RenderTarget;
		t.Created = true;
	}

	private static bool HasTargets(Target[] pool)
	{
		for (var i = 0; i < pool.Length; i++)
		{
			if (pool[i].Created)
				return true;
		}
		return false;
	}

	private static bool IsEmpty(SizeClass size)
	{
		foreach (var pool in size.Pools)
		{
			if (HasTargets(pool))
				return false;
		}
		return true;
	}

	private static void ReleaseTargets(Target[] pool)
	{
		for (var i = 0; i < pool.Length; i++)
		{
			if (pool[i].Created)
				pool[i].Resource.Dispose();
			pool[i] = default;
		}
	}

	public void Dispose()
	{
		foreach (var size in _classes)
		{
			foreach (var pool in size.Pools)
				ReleaseTargets(pool);
			size.RtvHeap.Dispose();
		}
		_classes.Clear();
		_current = null;
	}
}
