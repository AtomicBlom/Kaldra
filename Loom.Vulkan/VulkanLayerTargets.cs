using Silk.NET.Vulkan;
using VMASharp;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Loom.Vulkan;

/// <summary>
/// The renderer-private offscreen targets backing UI group-opacity layers (the <c>BeginLayer</c>/<c>EndLayer</c>
/// markers a <c>ctx.DrawList</c> submission carries). These are <em>not</em> graph resources — they live below the
/// graph seam so the render graph still sees exactly one <c>ui</c> pass and its hash never moves when an opacity
/// crosses 1.0. One full-surface colour image is pooled per (frame slot, nesting depth): nested layers need distinct
/// images at once, while sibling layers at the same depth reuse one (the re-acquire barrier in
/// <see cref="VulkanRenderContext"/> orders the prior composite read before the next clear). Images are kept across
/// frames (retired, not freed) so steady-state layering allocates nothing; they are recreated when the surface size
/// or colour format changes. Pooling at the pass colour format lets the already-baked <c>ui</c> pipeline render into
/// a layer unchanged.
/// </summary>
internal sealed unsafe class VulkanLayerTargets : IDisposable
{
	private readonly VulkanContext _ctx;
	private readonly Vk _vk;
	private readonly VulkanMemoryAllocator _allocator;

	internal struct Target
	{
		public Image Image;
		public ImageView View;
		public Allocation Allocation;
	}

	// One growable depth-indexed pool per frame slot. Slot isolation matches the per-slot draw-list rings: a slot is
	// only reused once its fence has signalled, so a target can never be cleared while an earlier frame still samples it.
	private readonly List<Target>[] _pools;
	private Extent2D _extent;
	private VkFormat _format;

	public VulkanLayerTargets(VulkanContext ctx, VulkanMemoryAllocator allocator, int framesInFlight)
	{
		_ctx = ctx;
		_vk = ctx.Vk;
		_allocator = allocator;
		_pools = new List<Target>[framesInFlight];
		for (var i = 0; i < framesInFlight; i++)
			_pools[i] = [];
	}

	/// <summary>
	/// Bind the pool to the current surface size + colour format. A change (window resize, HDR toggle) discards every
	/// pooled image — they would no longer match the resumed pass attachment. A no-op when unchanged, so the common
	/// per-frame call is free.
	/// </summary>
	public void Configure(Extent2D extent, VkFormat format)
	{
		if (_extent.Width == extent.Width && _extent.Height == extent.Height && _format == format)
			return;
		DisposeTargets();
		_extent = extent;
		_format = format;
	}

	/// <summary>Get (creating on first use) the layer target for <paramref name="slot"/> at nesting <paramref name="depth"/>.</summary>
	public Target Acquire(int slot, int depth)
	{
		var pool = _pools[slot];
		while (pool.Count <= depth)
			pool.Add(Create(pool.Count, slot));
		return pool[depth];
	}

	private Target Create(int depth, int slot)
	{
		Target t = default;
		ImageCreateInfo info = new()
		{
			SType = StructureType.ImageCreateInfo,
			ImageType = ImageType.Type2D,
			Format = _format,
			Extent = new Extent3D(_extent.Width, _extent.Height, 1),
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
			Format = _format,
			SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
		};
		_vk.CreateImageView(_ctx.Device, in viewInfo, null, out t.View).Check("vkCreateImageView(layer)");
		_ctx.SetObjectName(ObjectType.Image, t.Image.Handle, $"ui.layer#{slot}.{depth}");
		return t;
	}

	private void DisposeTargets()
	{
		foreach (var pool in _pools)
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
	}

	public void Dispose() => DisposeTargets();
}
