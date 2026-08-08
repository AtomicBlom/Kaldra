using System.Collections.Immutable;
using System.Text.Json;

namespace Loom.Graph;

/// <summary>
/// The descriptor-bound resource shape of a shader, read from the <c>slangc -reflection-json</c> sidecar that the
/// shader build emits next to each compiled module. It counts how many sampled inputs, storage images, storage
/// buffers, read-only input buffers, and uniform (constant) buffers the shader binds — so the app states only
/// pipeline intent (geometry/blend/filter/compute) and the backend derives the descriptor/root-signature layout
/// from reflection rather than the author hand-counting them. The JSON is the same across backends, so this parse
/// is shared; each backend maps the neutral counts onto its own layout objects.
/// </summary>
/// <param name="SampleCount">Sampled textures (read-only), bound as combined image samplers / an SRV table.</param>
/// <param name="StorageImageCount">Read-write textures (e.g. <c>RWTexture2D</c>), bound as storage images / UAV textures.</param>
/// <param name="StorageBufferCount">All structured / byte-address buffers, read-only and read-write (the Vulkan SSBO count).</param>
/// <param name="UniformBufferCount">Descriptor-bound constant buffers (distinct from a push/root constant).</param>
/// <param name="InputBufferCount">Read-only structured / byte-address buffers, the subset of <paramref name="StorageBufferCount"/> bound for reading (e.g. a compute input).</param>
/// <param name="StorageBufferStride">A read-write structured buffer's element stride in bytes (0 for a raw byte-address buffer or no sidecar).</param>
/// <param name="MaterialUniformSlot">
/// Which of the shader's uniform slots is the per-material parameter block — the ordinal, among this shader's
/// constant buffers in binding order, of the one declared with the reserved name
/// <see cref="MaterialParameterName"/>. <c>null</c> when the shader declares no material block, which is every
/// shader that takes its parameters from the pass or the draw. Nullable rather than a <c>-1</c> sentinel so that a
/// <c>default</c> reflection means "no material block" instead of "slot 0 is the material block".
/// </param>
/// <param name="MaterialParameterBytes">
/// The size of that block as the shader lays it out, including trailing padding to the cbuffer's alignment — the
/// number of bytes the backend stages per draw. 0 when there is no material block.
/// </param>
/// <param name="UniformBufferBytes">
/// Every constant buffer's declared size, in slot (binding) order — the bound a write into that slot must respect.
/// Prefer <see cref="UniformSlotBytes"/> over indexing this directly: it is <c>default</c> for a shader with no
/// sidecar, and reads as 0 ("unknown") rather than throwing.
/// </param>
public readonly record struct ShaderReflection(
	int SampleCount,
	int StorageImageCount,
	int StorageBufferCount,
	int UniformBufferCount,
	int InputBufferCount,
	int StorageBufferStride,
	int? MaterialUniformSlot = null,
	int MaterialParameterBytes = 0,
	ImmutableArray<int> UniformBufferBytes = default)
{
	/// <summary>
	/// The declared size of the constant buffer bound at <paramref name="slot"/> (the same slot ordinal
	/// <see cref="MaterialUniformSlot"/> uses), or 0 when the shader has no such slot or reflected no sidecar.
	/// <para>
	/// 0 means "unknown", never "holds nothing" — a caller sizing a write against this must treat 0 as "no reflected
	/// bound" and fall back, rather than concluding the buffer is empty and rejecting every write to it.
	/// </para>
	/// </summary>
	public int UniformSlotBytes(int slot) =>
		!UniformBufferBytes.IsDefaultOrEmpty && (uint)slot < (uint)UniformBufferBytes.Length ? UniformBufferBytes[slot] : 0;

	/// <summary>
	/// The reserved shader-side name of the per-material parameter block. A shader opts into the material parameter
	/// channel by declaring a constant buffer under this name; reflection then tells the backend which slot it is and
	/// how big it is, so neither the author nor the engine hand-counts. Convention is to declare it last (after the
	/// pass and per-draw blocks), but nothing depends on that — the slot is whatever reflection reports.
	/// </summary>
	public const string MaterialParameterName = "loomMaterial";

	/// <summary>Whether the shader declares a per-material parameter block.</summary>
	public bool HasMaterialParameters => MaterialUniformSlot.HasValue;

	/// <summary>The all-zero shape: nothing is known about the shader's bindings. Not "it binds nothing".</summary>
	public static ShaderReflection Empty => default;

	/// <summary>
	/// Parse slangc reflection JSON held in memory. Only the top-level <c>parameters</c> array is counted — each
	/// entry point re-lists the parameters it uses (with bindings but without the resource type), so counting those
	/// too would double-count. Only <c>descriptorTableSlot</c> parameters are counted (push constants, varyings, and
	/// per-field uniforms reach the GPU by other means); each is classified by its shape/access: a
	/// <c>constantBuffer</c>-typed slot is a uniform buffer, a structured/byte-address buffer is a storage buffer
	/// (further split into read-only inputs vs read-write), a read-write texture is a storage image, and any other
	/// sampled texture is a combined image sampler.
	/// </summary>
	public static ShaderReflection FromBytes(byte[] json)
	{
		using var doc = JsonDocument.Parse(json);
		int samplers = 0, storageImages = 0, storageBuffers = 0, uniformBuffers = 0, inputBuffers = 0, storageBufferStride = 0;
		// A uniform slot ordinal is a constant buffer's position *in binding order*, because that is the order the
		// backends bind them in (Vulkan's set-0 puts uniform buffers last as a block, ascending). Every cbuffer is
		// collected as (bindingIndex, byteSize) and the ordering resolved after the walk, rather than trusting the
		// JSON array to already be sorted.
		var materialBinding = -1;
		var materialBytes = 0;
		var cbuffers = new List<(int Binding, int Bytes)>();

		if (doc.RootElement.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
		{
			foreach (var p in parameters.EnumerateArray())
			{
				if (!p.TryGetProperty("binding", out var binding)
					|| !binding.TryGetProperty("kind", out var bindingKind)
					|| bindingKind.GetString() != "descriptorTableSlot")
					continue;
				if (!p.TryGetProperty("type", out var type)
					|| !type.TryGetProperty("kind", out var typeKind))
					continue;

				var kind = typeKind.GetString();
				// A descriptor-bound ConstantBuffer<T>, distinct from a pushConstantBuffer push.
				if (kind == "constantBuffer")
				{
					uniformBuffers++;
					var bindingIndex = binding.TryGetProperty("index", out var bi) ? bi.GetInt32() : int.MaxValue;
					var bytes = ConstantBufferByteSize(type);
					cbuffers.Add((bindingIndex, bytes));
					if (p.TryGetProperty("name", out var name) && name.GetString() == MaterialParameterName)
					{
						materialBinding = bindingIndex;
						materialBytes = bytes;
					}
					continue;
				}
				if (kind != "resource")
					continue;

				var baseShape = type.TryGetProperty("baseShape", out var shape) ? shape.GetString() : null;
				var readWrite = type.TryGetProperty("access", out var access) && access.GetString() == "readWrite";

				if (baseShape is "structuredBuffer" or "byteAddressBuffer")
				{
					storageBuffers++;
					if (readWrite)
					{
						if (baseShape == "structuredBuffer" && type.TryGetProperty("resultType", out var resultType))
							storageBufferStride = Math.Max(storageBufferStride, TypeByteSize(resultType)); // element size → a UAV's StructureByteStride
					}
					else
					{
						inputBuffers++; // a read-only structured/byte buffer, bound for reading
					}
				}
				else if (readWrite)
				{
					storageImages++; // RWTexture* — a storage image
				}
				else
				{
					samplers++;      // a sampled texture, bound as a combined image sampler
				}
			}
		}

		// Slot ordinals are positions in binding order, so sort once and let both the material slot and the per-slot
		// sizes read off the same ordering — they would otherwise disagree whenever the JSON is not already sorted.
		cbuffers.Sort((a, b) => a.Binding.CompareTo(b.Binding));
		var slotBytes = ImmutableArray.CreateBuilder<int>(cbuffers.Count);
		foreach (var (_, bytes) in cbuffers)
			slotBytes.Add(bytes);

		int? materialSlot = null;
		if (materialBinding >= 0)
			materialSlot = cbuffers.FindIndex(c => c.Binding == materialBinding);

		return new ShaderReflection(
			samplers, storageImages, storageBuffers, uniformBuffers, inputBuffers, storageBufferStride,
			materialSlot, materialSlot.HasValue ? materialBytes : 0, slotBytes.MoveToImmutable());
	}

	// A constant buffer's laid-out size in bytes, as the shader sees it — Slang reports it on the element's var
	// layout, already padded to the cbuffer alignment (a 68-byte struct reports 80). Fall back to summing the
	// element type's fields when the layout node is absent, which is what an older sidecar looks like.
	private static int ConstantBufferByteSize(JsonElement constantBufferType)
	{
		if (constantBufferType.TryGetProperty("elementVarLayout", out var elementVarLayout)
			&& elementVarLayout.TryGetProperty("binding", out var binding)
			&& binding.TryGetProperty("size", out var size))
			return size.GetInt32();
		return constantBufferType.TryGetProperty("elementType", out var elementType) ? TypeByteSize(elementType) : 0;
	}

	// The byte size of a Slang reflection type (scalar / vector / matrix / struct) — used to derive a structured
	// buffer's element stride. Returns 0 for an unsizeable type, which a backend treats as "no metadata" and may
	// fall back to a raw-buffer binding. A struct's stride is the extent of its last laid-out field,
	// max(field.offset + field.size); a structured buffer that sizes to 0 can leave a shader's GetDimensions
	// returning 0, so the count must be derived even for user-struct element types.
	private static int TypeByteSize(JsonElement type)
	{
		var kind = type.TryGetProperty("kind", out var k) ? k.GetString() : null;
		switch (kind)
		{
			case "scalar":
				var scalar = type.TryGetProperty("scalarType", out var s) ? s.GetString() : null;
				return scalar switch
				{
					"float16" or "int16" or "uint16" => 2,
					"float64" or "int64" or "uint64" => 8,
					_ => 4, // float32/int32/uint32 and friends
				};
			case "vector":
				var count = type.TryGetProperty("elementCount", out var e) ? e.GetInt32() : 0;
				return count * (type.TryGetProperty("elementType", out var et) ? TypeByteSize(et) : 0);
			case "matrix":
				var rows = type.TryGetProperty("rowCount", out var r) ? r.GetInt32() : 0;
				var cols = type.TryGetProperty("columnCount", out var c) ? c.GetInt32() : 0;
				return rows * cols * (type.TryGetProperty("elementType", out var met) ? TypeByteSize(met) : 4);
			case "struct":
				var size = 0;
				if (type.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
				{
					foreach (var field in fields.EnumerateArray())
					{
						if (field.TryGetProperty("binding", out var fb) && fb.TryGetProperty("offset", out var fo) && fb.TryGetProperty("size", out var fs))
							size = Math.Max(size, fo.GetInt32() + fs.GetInt32());
						else if (field.TryGetProperty("type", out var ft))
							size += TypeByteSize(ft); // no per-field layout: tight-pack as a fallback
					}
				}
				return size;
			default:
				return 0; // unknown — can't size generically
		}
	}
}
