using Silk.NET.Vulkan;
using VMASharp;
using VkFormat = Silk.NET.Vulkan.Format;

namespace BinaryVibrance.Kaldra.Backend.Vulkan;

/// <summary>
/// The renderer-private offscreen targets backing UI group-opacity layers (the <c>BeginLayer</c>/<c>EndLayer</c>
/// markers a <c>ctx.DrawList</c> submission carries). These are <em>not</em> graph resources — they live below the
/// graph seam so the render graph still sees exactly one <c>ui</c> pass and its hash never moves when an opacity
/// crosses 1.0. A layer target matches the scope its list draws into, so targets are pooled per (size, colour format)
/// — one frame can layer UI on targets of several sizes, a HUD on the backbuffer and a panel on its own texture — and
/// within a size per (frame slot, nesting depth): nested layers need distinct images at once, while sibling layers at
/// the same depth reuse one (the re-acquire barrier in <see cref="VulkanRenderContext"/> orders the prior composite
/// read before the next clear). Pooling at the pass colour format lets the already-baked <c>ui</c> pipeline render
/// into a layer unchanged.
/// <para>
/// Images are kept across frames, so steady-state layering allocates nothing, and are destroyed only in
/// <see cref="BeginFrame"/>, once their frame slot's earlier frame has completed — never while a recorded frame may
/// still use them. A size a slot stops layering at (one a window resize left behind) is retired after
/// <see cref="RetireAfterIdleFrames"/> of that slot's frames without it.
/// </para>
/// </summary>
internal sealed unsafe class VulkanLayerTargets : IDisposable
{
	/// <summary>
	/// How many of its own frames a slot keeps a size's targets without layering at it: long enough that a UI layering
	/// on and off (a fade now and then) keeps them, short enough that sizes a resize leaves behind go within a second.
	/// </summary>
	internal const int RetireAfterIdleFrames = 30;

	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly VulkanMemoryAllocator _allocator;
	private readonly int _slots;

	internal struct Target
	{
		public Image Image;
		public ImageView View;
		public Allocation Allocation;
	}

	// One size's targets: a growable depth-indexed pool per frame slot. Slot isolation matches the per-slot draw-list
	// rings: a slot is only reused once its fence has signalled, so a target can never be cleared while an earlier
	// frame still samples it, and a slot's targets can be destroyed when that slot begins a frame.
	private sealed class SizeClass(Extent2D extent, VkFormat format, int slots)
	{
		public readonly Extent2D Extent = extent;
		public readonly VkFormat Format = format;
		public readonly List<Target>[] Pools = NewPools(slots);
		public readonly bool[] UsedThisFrame = new bool[slots];
		public readonly int[] IdleFrames = new int[slots];

		public bool Matches(Extent2D extent, VkFormat format) =>
			Extent.Width == extent.Width && Extent.Height == extent.Height && Format == format;

		private static List<Target>[] NewPools(int slots)
		{
			var pools = new List<Target>[slots];
			for (var i = 0; i < slots; i++)
				pools[i] = [];
			return pools;
		}
	}

	private readonly List<SizeClass> _classes = [];
	private Extent2D _extent;
	private VkFormat _format;
	private SizeClass? _current; // the class matching _extent/_format, found on the first Acquire after Configure

	public VulkanLayerTargets(VulkanContext ctx, VulkanMemoryAllocator allocator, int framesInFlight)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_allocator = allocator;
		_slots = framesInFlight;
	}

	/// <summary>
	/// Start <paramref name="slot"/>'s frame. Its previous frame has completed, so nothing still recorded reads the
	/// slot's targets: a size the slot has gone <see cref="RetireAfterIdleFrames"/> frames without layering at has its
	/// targets for the slot destroyed here, and a size no slot holds targets for any more is forgotten.
	/// </summary>
	public void BeginFrame(int slot)
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
			if (++size.IdleFrames[slot] < RetireAfterIdleFrames || size.Pools[slot].Count == 0)
				continue;

			DestroyTargets(size.Pools[slot]);
			if (IsEmpty(size))
			{
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
	public void Configure(Extent2D extent, VkFormat format)
	{
		if (_extent.Width == extent.Width && _extent.Height == extent.Height && _format == format)
			return;
		_extent = extent;
		_format = format;
		_current = null;
	}

	/// <summary>Get (creating on first use) the layer target at the configured size for <paramref name="slot"/> at
	/// nesting <paramref name="depth"/>.</summary>
	public Target Acquire(int slot, int depth)
	{
		var size = _current ??= FindOrAddClass();
		size.UsedThisFrame[slot] = true;
		var pool = size.Pools[slot];
		while (pool.Count <= depth)
			pool.Add(Create(size, pool.Count, slot));
		return pool[depth];
	}

	/// <summary>How many sizes hold or may soon hold targets. Exposed for tests of the retirement policy.</summary>
	internal int SizeCount => _classes.Count;

	private SizeClass FindOrAddClass()
	{
		for (var i = 0; i < _classes.Count; i++)
		{
			if (_classes[i].Matches(_extent, _format))
				return _classes[i];
		}
		var added = new SizeClass(_extent, _format, _slots);
		_classes.Add(added);
		return added;
	}

	private Target Create(SizeClass size, int depth, int slot)
	{
		Target t = default;
		ImageCreateInfo info = new()
		{
			SType = StructureType.ImageCreateInfo,
			ImageType = ImageType.Type2D,
			Format = size.Format,
			Extent = new Extent3D(size.Extent.Width, size.Extent.Height, 1),
			MipLevels = 1,
			ArrayLayers = 1,
			Samples = SampleCountFlags.Count1Bit,
			Tiling = ImageTiling.Optimal,
			// Rendered into (children) then sampled (the composite blit) — the same two roles a sampled transient has.
			Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit,
			SharingMode = SharingMode.Exclusive,
			InitialLayout = ImageLayout.Undefined,
		};
		_vk.CreateImage(_ctx.Device, in info, null, out t.Image).Check("vkCreateImage(layer)");

		_vk.GetImageMemoryRequirements(_ctx.Device, t.Image, out var req);
		MemoryRequirements reqs = new() { Size = req.Size, Alignment = req.Alignment, MemoryTypeBits = req.MemoryTypeBits };
		t.Allocation = _allocator.AllocateMemory(in reqs, new AllocationCreateInfo { Usage = MemoryUsage.GPU_Only });
		t.Allocation.BindImageMemory(t.Image).Check("vmaBindImageMemory(layer)");

		ImageViewCreateInfo viewInfo = new()
		{
			SType = StructureType.ImageViewCreateInfo,
			Image = t.Image,
			ViewType = ImageViewType.Type2D,
			Format = size.Format,
			SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
		};
		_vk.CreateImageView(_ctx.Device, in viewInfo, null, out t.View).Check("vkCreateImageView(layer)");
		_ctx.SetObjectName(ObjectType.Image, t.Image.Handle, $"ui.layer#{slot}.{depth}@{size.Extent.Width}x{size.Extent.Height}");
		return t;
	}

	private static bool IsEmpty(SizeClass size)
	{
		foreach (var pool in size.Pools)
		{
			if (pool.Count > 0)
				return false;
		}
		return true;
	}

	// Destroy a pool's images. The layer composite binds a layer's view through a per-frame descriptor set, never a
	// cached one, so no cached set can name a view destroyed here.
	private void DestroyTargets(List<Target> pool)
	{
		foreach (var t in pool)
		{
			if (t.View.Handle != 0)
				_vk.DestroyImageView(_ctx.Device, t.View, null);
			if (t.Image.Handle != 0)
				_vk.DestroyImage(_ctx.Device, t.Image, null);
			t.Allocation?.Dispose();
		}
		pool.Clear();
	}

	public void Dispose()
	{
		foreach (var size in _classes)
		{
			foreach (var pool in size.Pools)
				DestroyTargets(pool);
		}
		_classes.Clear();
		_current = null;
	}
}
