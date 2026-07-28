using System.Text;
using Loom.Graph;

namespace Loom.Tests;

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
		var reflection = ShaderReflection.FromBytes(Sidecar(("view", 0, "constantBuffer", 64), ("draw", 1, "constantBuffer", 80)));

		reflection.UniformBufferCount.ShouldBe(2);
		reflection.HasMaterialParameters.ShouldBeFalse();
		reflection.MaterialUniformSlot.ShouldBeNull();
		reflection.MaterialParameterBytes.ShouldBe(0);
	}

	[Test]
	public void A_default_reflection_means_no_material_block()
	{
		// The trap this guards: with a -1 sentinel, default(ShaderReflection) would claim slot 0 is the material
		// block, and every shader without a sidecar (the DX12 built-in stand-ins) would bind garbage there.
		ShaderReflection.Empty.HasMaterialParameters.ShouldBeFalse();
		default(ShaderReflection).HasMaterialParameters.ShouldBeFalse();
	}

	[Test]
	public void The_material_slot_is_the_ordinal_among_constant_buffers_not_the_descriptor_index()
	{
		// Two samplers occupy descriptor slots 0-1, so the cbuffers sit at 2-4. loomMaterial is descriptor 4 but
		// uniform slot 2 — binding it at slot 4 would run off the end of a three-slot uniform sequence.
		var reflection = ShaderReflection.FromBytes(Sidecar(
			("albedoTex", 0, "resource", 0),
			("normalTex", 1, "resource", 0),
			("view", 2, "constantBuffer", 64),
			("draw", 3, "constantBuffer", 80),
			("loomMaterial", 4, "constantBuffer", 48)));

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
			("loomMaterial", 4, "constantBuffer", 48),
			("draw", 3, "constantBuffer", 80),
			("view", 2, "constantBuffer", 64)));

		reflection.MaterialUniformSlot.ShouldBe(2);
	}

	[Test]
	public void A_material_block_declared_first_is_slot_zero()
	{
		// Convention is to declare it last, but nothing depends on that — the slot is whatever reflection reports.
		var reflection = ShaderReflection.FromBytes(Sidecar(
			("loomMaterial", 0, "constantBuffer", 32),
			("view", 1, "constantBuffer", 64)));

		reflection.MaterialUniformSlot.ShouldBe(0);
		reflection.MaterialParameterBytes.ShouldBe(32);
	}

	[Test]
	public void The_block_size_comes_from_the_laid_out_padded_size()
	{
		// Slang reports the cbuffer-aligned size on elementVarLayout: a 68-byte struct occupies 80. Staging the
		// unpadded number would short the upload and leave the shader reading past the end of the ring slice.
		var real = ShaderReflection.FromBytes(Sidecar(("loomMaterial", 0, "constantBuffer", 80)));

		real.MaterialParameterBytes.ShouldBe(80);
	}

	[Test]
	public void The_real_mesh_sidecar_still_declares_no_material_block()
	{
		// Pins the reserved name against a shipped artifact: if a future shader edit named a block loomMaterial by
		// accident, or slangc stopped emitting parameter names, this notices.
		var path = Path.Combine(AppContext.BaseDirectory, "shaders", "mesh.json");
		if (!File.Exists(path))
			throw new TUnit.Core.Exceptions.SkipTestException($"reflection sidecar not found at {path}.");

		var reflection = ShaderReflection.FromBytes(File.ReadAllBytes(path));

		reflection.UniformBufferCount.ShouldBe(2); // view + draw — names are being read, so the parse is live
		reflection.HasMaterialParameters.ShouldBeFalse();
	}
}
