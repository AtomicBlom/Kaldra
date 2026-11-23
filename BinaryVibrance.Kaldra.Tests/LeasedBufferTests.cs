using BinaryVibrance.Kaldra.Concepts;
using BinaryVibrance.Kaldra.Core.Graph;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>The CPU half of <see cref="LeasedBuffer{T}"/>: lease/dispose bookkeeping, the swap-and-pop compaction
/// (and the index it hands the moved row's lease), and geometric growth. Pure data structure — no GPU needed; a
/// throwaway graph just mints the growable handle the buffer wraps.</summary>
public class LeasedBufferTests
{
	private static StorageBufferHandle GrowableHandle(int count = 4)
	{
		StorageBufferHandle handle = null!;
		RenderGraph.Define(g => handle = g.Storage<int>("test.rows", count, growable: true));
		return handle;
	}

	[Test]
	public void Lease_appends_rows_with_ascending_indices()
	{
		var buf = new LeasedBuffer<int>(GrowableHandle(), 4);
		var a = buf.Lease();
		var b = buf.Lease();
		var c = buf.Lease();

		buf.Count.ShouldBe(3);
		a.Index.ShouldBe(0);
		b.Index.ShouldBe(1);
		c.Index.ShouldBe(2);
	}

	[Test]
	public void Setting_Data_packs_into_the_row_and_round_trips()
	{
		var buf = new LeasedBuffer<int>(GrowableHandle(), 4);
		var a = buf.Lease();
		var b = buf.Lease();
		a.Data = 10;
		b.Data = 20;

		a.Data.ShouldBe(10);
		buf.AsSpan().ToArray().ShouldBe([10, 20]);
	}

	[Test]
	public void Disposing_a_middle_lease_swaps_the_last_row_into_the_gap()
	{
		var buf = new LeasedBuffer<int>(GrowableHandle(), 4);
		var a = buf.Lease(); a.Data = 10;
		var b = buf.Lease(); b.Data = 20;
		var c = buf.Lease(); c.Data = 30;

		b.Dispose();

		buf.Count.ShouldBe(2);
		b.Index.ShouldBe(-1);            // the disposed lease
		a.Index.ShouldBe(0);             // untouched
		c.Index.ShouldBe(1);             // the last row moved into b's slot, and was told its new index
		buf.AsSpan().ToArray().ShouldBe([10, 30]);
	}

	[Test]
	public void A_moved_lease_writes_to_its_new_slot()
	{
		var buf = new LeasedBuffer<int>(GrowableHandle(), 4);
		var a = buf.Lease(); a.Data = 10;
		var b = buf.Lease(); b.Data = 20;
		var c = buf.Lease(); c.Data = 30;

		b.Dispose();      // c moves from index 2 to index 1
		c.Data = 99;      // must land in slot 1, not the stale slot 2

		buf.AsSpan().ToArray().ShouldBe([10, 99]);
	}

	[Test]
	public void Disposing_the_last_lease_just_shrinks()
	{
		var buf = new LeasedBuffer<int>(GrowableHandle(), 4);
		var a = buf.Lease(); a.Data = 10;
		var b = buf.Lease(); b.Data = 20;

		b.Dispose();

		buf.Count.ShouldBe(1);
		a.Index.ShouldBe(0);
		buf.AsSpan().ToArray().ShouldBe([10]);
	}

	[Test]
	public void Disposing_twice_is_a_no_op()
	{
		var buf = new LeasedBuffer<int>(GrowableHandle(), 4);
		var a = buf.Lease(); a.Data = 10;
		var b = buf.Lease(); b.Data = 20;

		b.Dispose();
		Should.NotThrow(b.Dispose);

		buf.Count.ShouldBe(1);
		buf.AsSpan().ToArray().ShouldBe([10]);
	}

	[Test]
	public void Leasing_past_the_initial_capacity_grows_and_keeps_every_row()
	{
		var buf = new LeasedBuffer<int>(GrowableHandle(2), 2); // CPU + GPU both start at 2
		var leases = new BufferLease<int>[5];
		for (var i = 0; i < leases.Length; i++)
		{
			leases[i] = buf.Lease();
			leases[i].Data = i * 100;
		}

		buf.Count.ShouldBe(5);
		buf.AsSpan().ToArray().ShouldBe([0, 100, 200, 300, 400]);
		leases[4].Index.ShouldBe(4);
	}

	[Test]
	public void A_non_growable_handle_is_rejected()
	{
		StorageBufferHandle fixedHandle = null!;
		RenderGraph.Define(g => fixedHandle = g.Storage<int>("test.fixed", 4));
		Should.Throw<ArgumentException>(() => new LeasedBuffer<int>(fixedHandle, 4));
	}
}
