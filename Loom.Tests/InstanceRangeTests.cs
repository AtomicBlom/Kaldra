using Loom.Core.Geometry;
using Loom.Core.Hosting;

namespace Loom.Tests;

/// <summary>
/// A draw of a static instance buffer (<see cref="DrawCall.Static(InstanceBuffer, int, int)"/>) is held to the range
/// the buffer holds, and the technique drawing it to the stride it was laid out with
/// (<see cref="InstanceBuffer.CheckInstanceStride"/>): no backend reports a draw reading past the end, so these throw
/// before one is recorded. Needs no device.
/// </summary>
public class InstanceRangeTests
{
	private static readonly InstanceBuffer Ten = new(new Mesh(1), Count: 10, Stride: 32);

	[Test]
	public void A_range_inside_the_buffer_draws_it()
	{
		var all = DrawCall.Static(Ten);
		all.StaticFirst.ShouldBe(0);
		all.Count.ShouldBe(10);

		var tail = DrawCall.Static(Ten, 7, 3);
		tail.StaticFirst.ShouldBe(7);
		tail.Count.ShouldBe(3);
		tail.Stride.ShouldBe(32);

		// An empty range at the end is still inside it
		DrawCall.Static(Ten, 10, 0).Count.ShouldBe(0);
	}

	[Test]
	public void A_range_past_the_end_or_negative_throws()
	{
		Should.Throw<ArgumentOutOfRangeException>(() => DrawCall.Static(Ten, -1, 1));
		Should.Throw<ArgumentOutOfRangeException>(() => DrawCall.Static(Ten, 0, -1));
		Should.Throw<ArgumentOutOfRangeException>(() => DrawCall.Static(Ten, 11, 0));
		Should.Throw<ArgumentOutOfRangeException>(() => DrawCall.Static(Ten, 8, 3));
	}

	[Test]
	public void A_range_whose_end_overflows_an_int_throws()
	{
		var huge = Ten with { Count = int.MaxValue };
		// first + count wraps negative, which a summed check would let through
		Should.Throw<ArgumentOutOfRangeException>(() => DrawCall.Static(huge, int.MaxValue - 1, 2));
	}

	[Test]
	public void A_technique_with_another_stride_or_no_instance_stream_throws()
	{
		Should.NotThrow(() => Ten.CheckInstanceStride(32, "trees"));
		Should.Throw<InvalidOperationException>(() => Ten.CheckInstanceStride(48, "trees")).Message.ShouldContain("32-byte");
		Should.Throw<InvalidOperationException>(() => Ten.CheckInstanceStride(0, "trees")).Message.ShouldContain("no per-instance stream");
	}
}
