using BinaryVibrance.Kaldra.Input;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>The rumble output queue: FIFO drain, and the fire-and-forget drop-oldest policy that keeps the newest
/// effect when the queue fills (rather than throwing, unlike the input ring — rumble is output, not correctness).</summary>
public class RumbleQueueTests
{
	private static RumbleCommand Cmd(int pad) => new(pad, 1f, 1f, 0.1f);

	[Test]
	public void Enqueue_then_dequeue_preserves_fifo_order()
	{
		var queue = new RumbleQueue(8);
		queue.Enqueue(Cmd(0));
		queue.Enqueue(Cmd(1));

		queue.TryDequeue(out var a).ShouldBeTrue();
		queue.TryDequeue(out var b).ShouldBeTrue();
		a.PadIndex.ShouldBe(0);
		b.PadIndex.ShouldBe(1);
		queue.TryDequeue(out _).ShouldBeFalse();
	}

	[Test]
	public void A_full_queue_drops_the_oldest_to_keep_the_newest()
	{
		var queue = new RumbleQueue(2);
		queue.Enqueue(Cmd(0)); // oldest — evicted to make room
		queue.Enqueue(Cmd(1));
		queue.Enqueue(Cmd(2)); // pushes the queue past capacity

		queue.Count.ShouldBe(2);
		queue.TryDequeue(out var first).ShouldBeTrue();
		queue.TryDequeue(out var second).ShouldBeTrue();
		first.PadIndex.ShouldBe(1); // pad 0 was dropped
		second.PadIndex.ShouldBe(2);
	}

	[Test]
	public void Drain_copies_in_order_and_empties_the_queue()
	{
		var queue = new RumbleQueue(8);
		queue.Enqueue(Cmd(0));
		queue.Enqueue(Cmd(1));
		queue.Enqueue(Cmd(2));

		var buffer = new RumbleCommand[8];
		queue.Drain(buffer).ShouldBe(3);
		buffer[0].PadIndex.ShouldBe(0);
		buffer[2].PadIndex.ShouldBe(2);
		queue.Count.ShouldBe(0);
	}
}
