using BinaryVibrance.Kaldra.Graph;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// <see cref="BindingCache"/> is what keeps a cached descriptor set or bind group from outliving an object it
/// names. Vulkan and WebGPU both key their caches by object handle, and WebGPU's handles are table indices its allocator
/// hands straight back out: release a texture and the next view created gets its view's number. A bind group still
/// keyed by that number would then bind the destroyed view in the new texture's place — a wrong image, not an error.
/// The WebGPU backend runs only in a browser, so its use of the cache is pinned here, where the release sequence it
/// performs can be replayed on the CPU.
/// </summary>
public class BindingCacheTests
{
	private const ulong Layout = 3;
	private const ulong Sampler = 11;

	[Test]
	public void A_recycled_view_handle_misses_once_the_released_view_is_evicted()
	{
		// A draw list sampling texture A builds a group naming A's view, handle 7.
		var cache = new BindingCache();
		cache.Add(KeyNamingView(7), 100);

		// A is released: its groups go first, then handle 7 returns to the allocator. Texture B's view is created next
		// and is handed handle 7. Its draw must build a fresh group, not hit A's.
		var evicted = new List<ulong>();
		cache.Evict(BoundObject.View, 7, evicted);

		evicted.ShouldBe([100UL], "the group naming the released view should come back for the backend to release");
		cache.TryGetValue(KeyNamingView(7), out _).ShouldBeFalse("a key naming the recycled handle must miss once the released view's group is evicted");
	}

	[Test]
	public void Without_eviction_a_recycled_handle_hits_the_stale_binding()
	{
		// The hazard eviction exists for, stated as a fact about the key: the same number names the same key, whatever
		// object currently owns it. If this stopped being true the cache would need no eviction — and the other tests
		// would pass for the wrong reason.
		var cache = new BindingCache();
		cache.Add(KeyNamingView(7), 100);
		cache.TryGetValue(KeyNamingView(7), out var stale).ShouldBeTrue();
		stale.ShouldBe(100UL);
	}

	[Test]
	public void Eviction_takes_every_binding_naming_the_object_and_nothing_else()
	{
		var cache = new BindingCache();
		var withSampledView = KeyNamingView(7);
		var viewAmongOthers = new BindingKey(Layout, Sampler);
		viewAmongOthers.Add(0, BoundObject.View, 5);
		viewAmongOthers.Add(1, BoundObject.View, 7);
		viewAmongOthers.Add(2, BoundObject.Buffer, 40, window: 256);
		var otherView = KeyNamingView(8);
		cache.Add(withSampledView, 1);
		cache.Add(viewAmongOthers, 2);
		cache.Add(otherView, 3);

		var evicted = new List<ulong>();
		cache.Evict(BoundObject.View, 7, evicted);

		evicted.Order().ShouldBe([1UL, 2UL]);
		cache.Count.ShouldBe(1);
		cache.TryGetValue(otherView, out var kept).ShouldBeTrue();
		kept.ShouldBe(3UL);
	}

	[Test]
	public void Eviction_matches_the_kind_as_well_as_the_handle()
	{
		// WebGPU numbers each object table from zero, so a buffer and a view share every small handle. Releasing view 7
		// must not drop a group that binds buffer 7 — and releasing mesh buffer 7 must not drop one that samples view 7.
		var cache = new BindingCache();
		var buffer = new BindingKey(Layout);
		buffer.Add(0, BoundObject.Buffer, 7);
		cache.Add(buffer, 1);
		cache.Add(KeyNamingView(7), 2);

		var evicted = new List<ulong>();
		cache.Evict(BoundObject.View, 7, evicted);
		evicted.ShouldBe([2UL]);

		evicted.Clear();
		cache.Evict(BoundObject.Buffer, 7, evicted);
		evicted.ShouldBe([1UL]);
		cache.Count.ShouldBe(0);
	}

	[Test]
	public void Keys_differ_by_every_value_they_hold()
	{
		var view = KeyNamingView(7);
		view.Equals(KeyNamingView(7)).ShouldBeTrue();
		view.GetHashCode().ShouldBe(KeyNamingView(7).GetHashCode());

		var otherBinding = new BindingKey(Layout, Sampler);
		otherBinding.Add(1, BoundObject.View, 7);
		var otherSampler = new BindingKey(Layout, Sampler + 1);
		otherSampler.Add(0, BoundObject.View, 7);
		var otherLayout = new BindingKey(Layout + 1, Sampler);
		otherLayout.Add(0, BoundObject.View, 7);
		var buffer = new BindingKey(Layout, Sampler);
		buffer.Add(0, BoundObject.Buffer, 7);
		view.Equals(otherBinding).ShouldBeFalse();
		view.Equals(otherSampler).ShouldBeFalse();
		view.Equals(otherLayout).ShouldBeFalse();
		view.Equals(buffer).ShouldBeFalse();

		// Two windows onto one buffer are two descriptors.
		var narrow = new BindingKey(Layout);
		narrow.Add(0, BoundObject.Buffer, 40, window: 64);
		var wide = new BindingKey(Layout);
		wide.Add(0, BoundObject.Buffer, 40, window: 256);
		narrow.Equals(wide).ShouldBeFalse();
	}

	[Test]
	public void A_key_refuses_an_entry_past_its_capacity()
	{
		var key = new BindingKey(Layout);
		for (var binding = 0; binding < BindingKey.MaxEntries; binding++)
			key.Add(binding, BoundObject.View, (ulong)binding);
		Should.Throw<InvalidOperationException>(() => key.Add(BindingKey.MaxEntries, BoundObject.View, 99));
	}

	private static BindingKey KeyNamingView(ulong view)
	{
		var key = new BindingKey(Layout, Sampler);
		key.Add(0, BoundObject.View, view);
		return key;
	}
}
