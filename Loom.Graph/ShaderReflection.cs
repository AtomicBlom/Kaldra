using System.Collections.Immutable;
using System.Text.Json;

namespace Loom.Graph;

/// <summary>
/// The descriptor-bound resource shape of a shader, read from the <c>slangc -reflection-json</c> sidecar that the
/// shader build emits next to each compiled module. Every parameter is recorded with the slot the shader actually
/// gave it (<see cref="Bindings"/>), so a backend builds its descriptor layout from real indices rather than
/// assuming a category order the author had to hand-number to match. The per-category counts remain available as
/// derived views, computed from the same array a layout is built from, so the two cannot disagree.
/// <para>
/// The JSON is target-specific, not shared: see <see cref="ShaderBindingTarget"/>. Which target a reflection was
/// parsed for is carried in <see cref="Target"/> and must be checked before its indices are used.
/// </para>
/// </summary>
public readonly record struct ShaderReflection
{
	private ShaderReflection(ImmutableArray<ShaderBinding> bindings, ShaderBindingTarget target)
	{
		Bindings = bindings;
		Target = target;

		// Derive every count in one pass and store it: these are read per draw on the record path, so recomputing
		// them per access would put a scan of the binding array in the hot loop.
		var slotBytes = ImmutableArray.CreateBuilder<int>();
		var uniformSlot = 0;
		foreach (var binding in bindings)
		{
			switch (binding.Kind)
			{
				case ShaderBindingKind.SampledTexture:
					SampleCount++;
					break;
				case ShaderBindingKind.StorageImage:
					StorageImageCount++;
					break;
				case ShaderBindingKind.StorageBufferReadOnly:
					StorageBufferCount++;
					InputBufferCount++;
					break;
				case ShaderBindingKind.StorageBufferReadWrite:
					StorageBufferCount++;
					StorageBufferStride = Math.Max(StorageBufferStride, binding.ElementStride);
					break;
				case ShaderBindingKind.UniformBuffer:
					UniformBufferCount++;
					slotBytes.Add(binding.ByteSize);
					if (binding.Name == MaterialParameterName)
					{
						MaterialUniformSlot = uniformSlot;
						MaterialParameterBytes = binding.ByteSize;
					}
					uniformSlot++;
					break;
			}
		}
		UniformBufferBytes = slotBytes.ToImmutable();
	}

	/// <summary>
	/// Every descriptor-bound parameter the shader declares, ascending by <see cref="ShaderBinding.Index"/>. The
	/// single source of truth: the counts below are views over this.
	/// </summary>
	public ImmutableArray<ShaderBinding> Bindings { get; }

	/// <summary>Which compilation target's numbering <see cref="Bindings"/> uses.</summary>
	public ShaderBindingTarget Target { get; }

	/// <summary>Sampled textures (read-only), bound as combined image samplers / an SRV table.</summary>
	public int SampleCount { get; }

	/// <summary>Read-write textures (e.g. <c>RWTexture2D</c>), bound as storage images / UAV textures.</summary>
	public int StorageImageCount { get; }

	/// <summary>All structured / byte-address buffers, read-only and read-write (the Vulkan SSBO count).</summary>
	public int StorageBufferCount { get; }

	/// <summary>Descriptor-bound constant buffers (distinct from a push/root constant).</summary>
	public int UniformBufferCount { get; }

	/// <summary>Read-only structured / byte-address buffers, the subset of <see cref="StorageBufferCount"/> bound for reading (e.g. a compute input).</summary>
	public int InputBufferCount { get; }

	/// <summary>A read-write structured buffer's element stride in bytes (0 for a raw byte-address buffer or no sidecar).</summary>
	public int StorageBufferStride { get; }

	/// <summary>
	/// Which of the shader's uniform slots is the per-material parameter block — the ordinal, among this shader's
	/// constant buffers in binding order, of the one declared with the reserved name
	/// <see cref="MaterialParameterName"/>. <c>null</c> when the shader declares no material block, which is every
	/// shader that takes its parameters from the pass or the draw. Nullable rather than a <c>-1</c> sentinel so that a
	/// <c>default</c> reflection means "no material block" instead of "slot 0 is the material block".
	/// </summary>
	public int? MaterialUniformSlot { get; }

	/// <summary>
	/// The size of that block as the shader lays it out, including trailing padding to the cbuffer's alignment — the
	/// number of bytes the backend stages per draw. 0 when there is no material block.
	/// </summary>
	public int MaterialParameterBytes { get; }

	/// <summary>
	/// Every constant buffer's declared size, in slot (binding) order — the bound a write into that slot must respect.
	/// Prefer <see cref="UniformSlotBytes"/> over indexing this directly: it is <c>default</c> for a shader with no
	/// sidecar, and reads as 0 ("unknown") rather than throwing.
	/// </summary>
	public ImmutableArray<int> UniformBufferBytes { get; }

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
	/// Build a reflection from bindings resolved some other way than parsing a sidecar — a backend that recovers its
	/// real assignments from the emitted shader text, or a test. <paramref name="bindings"/> need not be sorted.
	/// </summary>
	public static ShaderReflection FromBindings(IEnumerable<ShaderBinding> bindings, ShaderBindingTarget target)
	{
		ArgumentNullException.ThrowIfNull(bindings);
		return new ShaderReflection([.. bindings.OrderBy(b => b.Index)], target);
	}

	/// <summary>
	/// Parse slangc reflection JSON held in memory. Only the top-level <c>parameters</c> array is read — each
	/// entry point re-lists the parameters it uses (with bindings but without the resource type), so reading those
	/// too would double-count. Only <c>descriptorTableSlot</c> parameters are read (push constants, varyings, and
	/// per-field uniforms reach the GPU by other means); each is classified by its shape/access: a
	/// <c>constantBuffer</c>-typed slot is a uniform buffer, a structured/byte-address buffer is a storage buffer
	/// (further split into read-only inputs vs read-write), a read-write texture is a storage image, and any other
	/// sampled texture is a combined image sampler.
	/// </summary>
	/// <param name="json">The sidecar's bytes.</param>
	/// <param name="target">
	/// Which target emitted <paramref name="json"/>. Required rather than inferred: the sidecars are indistinguishable
	/// by content but their indices are not interchangeable.
	/// </param>
	public static ShaderReflection FromBytes(byte[] json, ShaderBindingTarget target)
	{
		using var doc = JsonDocument.Parse(json);
		var bindings = ImmutableArray.CreateBuilder<ShaderBinding>();

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

				var name = p.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
				// An older sidecar may omit the index; int.MaxValue sorts it last rather than colliding at 0.
				var index = binding.TryGetProperty("index", out var indexElement) ? indexElement.GetInt32() : int.MaxValue;
				var slotCount = binding.TryGetProperty("count", out var countElement) ? Math.Max(1, countElement.GetInt32()) : 1;

				var kind = typeKind.GetString();
				// A descriptor-bound ConstantBuffer<T>, distinct from a pushConstantBuffer push.
				if (kind == "constantBuffer")
				{
					bindings.Add(new ShaderBinding(name, index, ShaderBindingKind.UniformBuffer, slotCount, ByteSize: ConstantBufferByteSize(type)));
					continue;
				}
				if (kind != "resource")
					continue;

				var baseShape = type.TryGetProperty("baseShape", out var shape) ? shape.GetString() : null;
				var readWrite = type.TryGetProperty("access", out var access) && access.GetString() == "readWrite";

				if (baseShape is "structuredBuffer" or "byteAddressBuffer")
				{
					// A read-write structured buffer's element size becomes a UAV's StructureByteStride; a raw
					// byte-address buffer has no element type and stays 0.
					var stride = readWrite && baseShape == "structuredBuffer" && type.TryGetProperty("resultType", out var resultType)
						? TypeByteSize(resultType)
						: 0;
					var bufferKind = readWrite ? ShaderBindingKind.StorageBufferReadWrite : ShaderBindingKind.StorageBufferReadOnly;
					bindings.Add(new ShaderBinding(name, index, bufferKind, slotCount, ElementStride: stride));
				}
				else
				{
					var textureKind = readWrite ? ShaderBindingKind.StorageImage : ShaderBindingKind.SampledTexture;
					bindings.Add(new ShaderBinding(name, index, textureKind, slotCount));
				}
			}
		}

		// Slot ordinals are positions in binding order, so sort once and let the layout, the per-slot sizes, and the
		// material slot all read off the same ordering — they would otherwise disagree whenever the JSON is not
		// already sorted, which nothing guarantees.
		bindings.Sort((a, b) => a.Index.CompareTo(b.Index));
		return new ShaderReflection(bindings.ToImmutable(), target);
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
