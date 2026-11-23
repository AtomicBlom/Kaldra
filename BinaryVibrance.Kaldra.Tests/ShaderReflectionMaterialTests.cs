using System.Text;
using BinaryVibrance.Kaldra.Graph;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// Reading the reserved per-material parameter block out of a <c>slangc -reflection-json</c> sidecar. The slot has to
/// be the block's ordinal <em>among the shader's constant buffers</em>, not its raw descriptor index — a shader with
/// samplers numbers its cbuffers well above 0 — and it has to survive the sidecar listing parameters out of order.
/// </summary>
public class ShaderReflectionMaterialTests
{
	// A minimal sidecar: one descriptor-table parameter per (name, index, kind), plus a laid-out size for cbuffers.
	private static byte[] Sidecar(params (string Name, int Index, string Kind, int Size)[] parameters)
	{
		var sb = new StringBuilder("{\"parameters\":[");
		for (var i = 0; i < parameters.Length; i++)
		{
			var (name, index, kind, size) = parameters[i];
			if (i > 0)
				sb.Append(',');
			sb.Append("{\"name\":\"").Append(name).Append('"')
				.Append(",\"binding\":{\"kind\":\"descriptorTableSlot\",\"index\":").Append(index).Append('}')
				.Append(",\"type\":{\"kind\":\"").Append(kind).Append('"');
			if (kind == "constantBuffer")
			{
				sb.Append(",\"elementType\":{\"kind\":\"struct\",\"name\":\"Block\",\"fields\":[]}")
					.Append(",\"elementVarLayout\":{\"binding\":{\"kind\":\"uniform\",\"offset\":0,\"size\":").Append(size).Append("}}");
			}
			else
			{
				sb.Append(",\"baseShape\":\"texture2D\",\"access\":\"read\"");
			}
			sb.Append("}}");
		}
		return Encoding.UTF8.GetBytes(sb.Append("]}").ToString());
	}

	[Test]
	public void A_shader_with_no_material_block_reports_no_slot()
	{
		var reflection = ShaderReflection.FromBytes(Sidecar(("view", 0, "constantBuffer", 64), ("draw", 1, "constantBuffer", 80)), ShaderBindingTarget.SpirV);

		reflection.UniformBufferCount.ShouldBe(2);
		reflection.HasMaterialParameters.ShouldBeFalse();
		reflection.MaterialUniformSlot.ShouldBeNull();
		reflection.MaterialParameterBytes.ShouldBe(0);
	}

	[Test]
	public void A_default_reflection_means_no_material_block()
	{
		// The trap this guards: with a -1 sentinel, default(ShaderReflection) would claim slot 0 is the material
		// block, so any shader whose bindings are unknown would bind garbage there.
		ShaderReflection.Empty.HasMaterialParameters.ShouldBeFalse();
		default(ShaderReflection).HasMaterialParameters.ShouldBeFalse();
	}

	[Test]
	public void The_material_slot_is_the_ordinal_among_constant_buffers_not_the_descriptor_index()
	{
		// Two samplers occupy descriptor slots 0-1, so the cbuffers sit at 2-4. kaldraMaterial is descriptor 4 but
		// uniform slot 2 — binding it at slot 4 would run off the end of a three-slot uniform sequence.
		var reflection = ShaderReflection.FromBytes(Sidecar(
			("albedoTex", 0, "resource", 0),
			("normalTex", 1, "resource", 0),
			("view", 2, "constantBuffer", 64),
			("draw", 3, "constantBuffer", 80),
			("kaldraMaterial", 4, "constantBuffer", 48)), ShaderBindingTarget.SpirV);

		reflection.SampleCount.ShouldBe(2);
		reflection.UniformBufferCount.ShouldBe(3);
		reflection.MaterialUniformSlot.ShouldBe(2);
		reflection.MaterialParameterBytes.ShouldBe(48);
	}

	[Test]
	public void The_material_slot_is_resolved_by_binding_order_not_listing_order()
	{
		// Same shader, sidecar listing the material block first. The slot must still be 2.
		var reflection = ShaderReflection.FromBytes(Sidecar(
			("kaldraMaterial", 4, "constantBuffer", 48),
			("draw", 3, "constantBuffer", 80),
			("view", 2, "constantBuffer", 64)), ShaderBindingTarget.SpirV);

		reflection.MaterialUniformSlot.ShouldBe(2);
	}

	[Test]
	public void A_material_block_declared_first_is_slot_zero()
	{
		// Convention is to declare it last, but nothing depends on that — the slot is whatever reflection reports.
		var reflection = ShaderReflection.FromBytes(Sidecar(
			("kaldraMaterial", 0, "constantBuffer", 32),
			("view", 1, "constantBuffer", 64)), ShaderBindingTarget.SpirV);

		reflection.MaterialUniformSlot.ShouldBe(0);
		reflection.MaterialParameterBytes.ShouldBe(32);
	}

	[Test]
	public void The_block_size_comes_from_the_laid_out_padded_size()
	{
		// Slang reports the cbuffer-aligned size on elementVarLayout: a 68-byte struct occupies 80. Staging the
		// unpadded number would short the upload and leave the shader reading past the end of the ring slice.
		var real = ShaderReflection.FromBytes(Sidecar(("kaldraMaterial", 0, "constantBuffer", 80)), ShaderBindingTarget.SpirV);

		real.MaterialParameterBytes.ShouldBe(80);
	}

	[Test]
	public void Every_constant_buffer_reports_its_size_in_slot_order()
	{
		// Sizes are read for all cbuffers, not just the reserved block, and land in binding order even when the
		// sidecar lists them out of order — the same ordering the material slot ordinal uses.
		var reflection = ShaderReflection.FromBytes(Sidecar(
			("draw", 5, "constantBuffer", 80),
			("view", 2, "constantBuffer", 64)), ShaderBindingTarget.SpirV);

		reflection.UniformSlotBytes(0).ShouldBe(64); // view, the lower binding
		reflection.UniformSlotBytes(1).ShouldBe(80); // draw
	}

	[Test]
	public void An_unknown_slot_size_reads_as_zero_rather_than_throwing()
	{
		// 0 means "no reflected bound", and a caller sizing a write must fall back rather than conclude the buffer
		// holds nothing. A shader with no sidecar has no sizes at all, so this must not throw on the default array.
		ShaderReflection.Empty.UniformSlotBytes(0).ShouldBe(0);
		default(ShaderReflection).UniformSlotBytes(3).ShouldBe(0);

		var reflection = ShaderReflection.FromBytes(Sidecar(("view", 0, "constantBuffer", 64)), ShaderBindingTarget.SpirV);
		reflection.UniformSlotBytes(1).ShouldBe(0);
		reflection.UniformSlotBytes(-1).ShouldBe(0);
	}
}
