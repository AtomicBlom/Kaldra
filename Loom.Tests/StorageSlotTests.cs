using System.Numerics;
using System.Text;
using Loom.Core.Graph;
using Loom.Graph;

namespace Loom.Tests;

/// <summary>
/// A pass's <c>.Reads</c> storage buffers bind to a technique's storage slots by position, its first slot the first the
/// pass reads. <see cref="ShaderBindingMap.CheckPassStorage"/> holds each buffer's element size to the slot it lands on,
/// so a technique declaring only a later one of a pass's buffers throws rather than reading an earlier one. Runs on
/// hand-built reflection sidecars.
/// </summary>
public class StorageSlotTests
{
	private readonly record struct Light(Vector4 Position, Vector4 Colour, Vector4 Direction, Vector4 Cone);

	private readonly record struct Range(uint First, uint Count);

	private readonly record struct Packed3(Vector4 A, Vector3 B);

	// A float3 with the padding std430 gives it in an array
	private readonly record struct Padded3(Vector4 A, Vector4 B);

	[Test]
	public void A_read_only_structured_buffer_is_sized_from_its_element()
	{
		var map = Map(("lights", 0, LightType), ("tints", 1, Float4Type));
		map.StorageBuffers[0].ElementStride.ShouldBe(64);
		map.StorageBuffers[0].ElementAlignment.ShouldBe(16);
		map.StorageBuffers[1].ElementStride.ShouldBe(16);
	}

	[Test]
	public void Slots_declared_in_the_order_the_pass_reads_them_pass()
	{
		var pass = Pass(g => [g.Storage<Light>("lights", 4), g.Storage<Range>("clusters", 4), g.Storage<Vector4>("tint", 4)]);

		Should.NotThrow(() => Map(("lights", 0, LightType), ("clusters", 1, UInt2Type), ("tints", 2, Float4Type)).CheckPassStorage("ribbon", pass));
		// Fewer slots than the pass reads take its first ones
		Should.NotThrow(() => Map(("lights", 0, LightType), ("clusters", 1, UInt2Type)).CheckPassStorage("terrain", pass));
	}

	[Test]
	public void A_technique_declaring_only_a_later_buffer_throws()
	{
		var pass = Pass(g => [g.Storage<Light>("lights", 4), g.Storage<Vector4>("tint", 4)]);

		// Its one slot reads float4s, but takes the pass's first buffer, the lights
		Should.Throw<InvalidOperationException>(() => Map(("tints", 0, Float4Type)).CheckPassStorage("tinted", pass))
			.Message.ShouldContain("'lights'");
	}

	[Test]
	public void An_element_packed_or_padded_to_its_alignment_is_accepted()
	{
		// A float4 then a float3: 28 bytes packed, 32 as std430 lays out an array of it
		Should.NotThrow(() => Map(("e", 0, Float4Float3Type)).CheckPassStorage("t", Pass(g => [g.Storage<Packed3>("p", 4)])));
		Should.NotThrow(() => Map(("e", 0, Float4Float3Type)).CheckPassStorage("t", Pass(g => [g.Storage<Padded3>("p", 4)])));
		// A uint padded to 8 is not a uint: nothing in it aligns past 4
		Should.Throw<InvalidOperationException>(() => Map(("e", 0, UIntType)).CheckPassStorage("t", Pass(g => [g.Storage<Range>("r", 4)])));
	}

	[Test]
	public void A_compute_techniques_read_write_slots_are_skipped_when_holding_read_only_ones()
	{
		var pass = Pass(g => [g.Storage<Vector4>("map", 4)]);
		// The read-write slot comes first but takes the dispatch's buffer; the read-only one takes the pass's first
		Should.NotThrow(() => Map(("out", 0, LightType, true), ("map", 1, Float4Type, false)).CheckPassStorage("place", pass, readOnlyOnly: true));
	}

	[Test]
	public void A_dispatch_supplies_exactly_the_read_write_storage_its_technique_declares()
	{
		ResourceHandle first = null!, second = null!, image = null!;
		RenderGraph.Define(g =>
		{
			first = g.Buffer<Vector4>("first", 4);
			second = g.Buffer<Vector4>("second", 4);
			image = g.Persistent("image", Format.Rgba8Srgb);
			g.Pass("draw").Writes(g.Backbuffer).Draw(_ => { });
			g.Present(g.Backbuffer);
		});
		// One read-write slot, which the dispatch fills, and one read-only, which the pass's .Reads fills
		var map = Map(("out", 0, Float4Type, true), ("map", 1, Float4Type, false));

		Should.NotThrow(() => map.CheckDispatchStorage("place", [first]));
		// A surplus buffer would be bound nowhere, a missing one leaves the slot unbound
		Should.Throw<InvalidOperationException>(() => map.CheckDispatchStorage("place", [first, second])).Message.ShouldContain("supplies 2");
		Should.Throw<InvalidOperationException>(() => map.CheckDispatchStorage("place", [])).Message.ShouldContain("supplies 0");
		// An image where the shader declares no RWTexture
		Should.Throw<InvalidOperationException>(() => map.CheckDispatchStorage("place", [first, image])).Message.ShouldContain("storage image");
	}

	private const string Float4Type = "{\"kind\":\"vector\",\"elementCount\":4,\"elementType\":{\"kind\":\"scalar\",\"scalarType\":\"float32\"}}";
	private const string Float3Type = "{\"kind\":\"vector\",\"elementCount\":3,\"elementType\":{\"kind\":\"scalar\",\"scalarType\":\"float32\"}}";
	private const string UInt2Type = "{\"kind\":\"vector\",\"elementCount\":2,\"elementType\":{\"kind\":\"scalar\",\"scalarType\":\"uint32\"}}";
	private const string UIntType = "{\"kind\":\"scalar\",\"scalarType\":\"uint32\"}";
	private static readonly string LightType = Struct((Float4Type, 0, 16), (Float4Type, 16, 16), (Float4Type, 32, 16), (Float4Type, 48, 16));
	private static readonly string Float4Float3Type = Struct((Float4Type, 0, 16), (Float3Type, 16, 12));

	private static string Struct(params (string Type, int Offset, int Size)[] fields) =>
		"{\"kind\":\"struct\",\"fields\":[" + string.Join(',', fields.Select(f =>
			$"{{\"type\":{f.Type},\"binding\":{{\"kind\":\"uniform\",\"offset\":{f.Offset},\"size\":{f.Size}}}}}")) + "]}";

	// A sidecar of structured buffers, each its name, binding, element type and whether it is read-write
	private static ShaderBindingMap Map(params (string Name, int Index, string Element, bool ReadWrite)[] buffers)
	{
		var sb = new StringBuilder("{\"parameters\":[");
		sb.AppendJoin(',', buffers.Select(b =>
			$"{{\"name\":\"{b.Name}\",\"binding\":{{\"kind\":\"descriptorTableSlot\",\"index\":{b.Index}}}," +
			$"\"type\":{{\"kind\":\"resource\",\"baseShape\":\"structuredBuffer\"{(b.ReadWrite ? ",\"access\":\"readWrite\"" : "")},\"resultType\":{b.Element}}}}}"));
		return ShaderBindingMap.From(ShaderReflection.FromBytes(Encoding.UTF8.GetBytes(sb.Append("]}").ToString()), ShaderBindingTarget.SpirV));
	}

	private static ShaderBindingMap Map(params (string Name, int Index, string Element)[] buffers) =>
		Map([.. buffers.Select(b => (b.Name, b.Index, b.Element, false))]);

	// A pass that .Reads the given storage buffers, in order
	private static GraphPass Pass(Func<GraphBuilder, StorageBufferHandle[]> declare) =>
		RenderGraph.Define(g =>
		{
			var storage = declare(g);
			var pass = g.Pass("draw");
			foreach (var buffer in storage)
				pass.Reads(buffer);
			pass.Writes(g.Backbuffer).Draw(_ => { });
			g.Present(g.Backbuffer);
		}).Passes.Single(p => p.Name == "draw");
}
