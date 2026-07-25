using Loom.Input;

namespace Loom.Tests;

/// <summary>The preallocated event ring: FIFO order across wraparound, fail-loud overflow, and the drain semantics
/// (full and partial) the host's per-frame path relies on.</summary>
public class InputEventRingTests
{
	private static InputEvent Ev(uint tag) => InputEvent.Text(tag);

	[Test]
	public void Enqueue_then_dequeue_preserves_fifo_order()
	{
		var ring = new InputEventRing(8);
		ring.Enqueue(Ev(1));
		ring.Enqueue(Ev(2));
		ring.Enqueue(Ev(3));

		ring.Count.ShouldBe(3);
		ring.IsEmpty.ShouldBeFalse();

		ring.TryDequeue(out var a).ShouldBeTrue();
		ring.TryDequeue(out var b).ShouldBeTrue();
		ring.TryDequeue(out var c).ShouldBeTrue();
		a.CodePoint.ShouldBe(1u);
		b.CodePoint.ShouldBe(2u);
		c.CodePoint.ShouldBe(3u);

		ring.TryDequeue(out _).ShouldBeFalse();
		ring.IsEmpty.ShouldBeTrue();
	}

	[Test]
	public void Enqueue_at_capacity_fails_loud()
	{
		var ring = new InputEventRing(2);
		ring.Enqueue(Ev(1));
		ring.Enqueue(Ev(2));

		// A dropped press/release would strand held state, so overflow throws rather than silently discarding.
		Should.Throw<InvalidOperationException>(() => ring.Enqueue(Ev(3)));
	}

	[Test]
	public void Fifo_order_survives_wraparound()
	{
		// Capacity 4: fill, drain two, enqueue two more so the head/tail indices cross the buffer boundary.
		var ring = new InputEventRing(4);
		ring.Enqueue(Ev(1));
		ring.Enqueue(Ev(2));
		ring.Enqueue(Ev(3));
		ring.Enqueue(Ev(4));

		ring.TryDequeue(out var first).ShouldBeTrue();
		ring.TryDequeue(out var second).ShouldBeTrue();
		first.CodePoint.ShouldBe(1u);
		second.CodePoint.ShouldBe(2u);

		ring.Enqueue(Ev(5)); // reuses the two freed slots at the front of the buffer
		ring.Enqueue(Ev(6));

		var drained = new InputEvent[4];
		ring.Drain(drained).ShouldBe(4);
		drained[0].CodePoint.ShouldBe(3u);
		drained[1].CodePoint.ShouldBe(4u);
		drained[2].CodePoint.ShouldBe(5u);
		drained[3].CodePoint.ShouldBe(6u);
	}

	[Test]
	public void Drain_empties_the_ring_and_reports_the_count()
	{
		var ring = new InputEventRing(8);
		ring.Enqueue(Ev(1));
		ring.Enqueue(Ev(2));

		var buffer = new InputEvent[8];
		ring.Drain(buffer).ShouldBe(2);
		ring.Count.ShouldBe(0);
		ring.IsEmpty.ShouldBeTrue();
	}

	[Test]
	public void A_short_destination_drains_a_prefix_and_leaves_the_rest_in_order()
	{
		var ring = new InputEventRing(8);
		for (uint i = 1; i <= 4; i++)
			ring.Enqueue(Ev(i));

		var two = new InputEvent[2];
		ring.Drain(two).ShouldBe(2);
		two[0].CodePoint.ShouldBe(1u);
		two[1].CodePoint.ShouldBe(2u);

		// The uncopied events keep both their order and their place at the head of the ring.
		ring.Count.ShouldBe(2);
		ring.TryDequeue(out var third).ShouldBeTrue();
		third.CodePoint.ShouldBe(3u);
	}

	[Test]
	public void Clear_discards_all_pending_events()
	{
		var ring = new InputEventRing(8);
		ring.Enqueue(Ev(1));
		ring.Enqueue(Ev(2));

		ring.Clear();

		ring.Count.ShouldBe(0);
		ring.IsEmpty.ShouldBeTrue();
		ring.TryDequeue(out _).ShouldBeFalse();
	}
}
