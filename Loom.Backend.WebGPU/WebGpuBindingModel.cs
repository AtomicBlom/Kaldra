using System.Text.Json;
using System.Text.RegularExpressions;

using Loom.Graph;

namespace Loom.Backend.WebGPU;

/// <summary>The category of a single WGSL resource binding, deciding the bind-group-layout entry it produces.</summary>
internal enum WebGpuBindingKind
{
	UniformBuffer,
	Texture,
	DepthTexture,
	Sampler,
	ComparisonSampler,
	StorageBufferReadOnly,
	StorageBufferReadWrite,
	StorageTexture,
}

/// <summary>One bind-group-layout entry: the WGSL <c>@binding</c> slot, its category, and a kind-specific extra
/// (a storage texture's format id; zero otherwise).</summary>
internal readonly record struct WebGpuLayoutEntry(int Binding, WebGpuBindingKind Kind, int Extra);

/// <summary>
/// The bind-group shape of a WGSL technique, derived by cross-referencing the emitted WGSL text (authoritative for
/// the actual <c>@group</c>/<c>@binding</c> numbering Slang assigned) with the <c>.wgsl.json</c> reflection sidecar
/// (authoritative for the app-facing slot <em>order</em> — its <c>parameters</c> are listed in source order, i.e.
/// the order a pass passes its inputs). Slang auto-numbers WGSL bindings (there are no <c>[[vk::binding]]</c>
/// attributes under the WGSL target), splits each combined <c>Sampler2D</c> into a texture + sampler pair, and
/// lowers a <c>[[vk::push_constant]]</c> block to a plain uniform buffer — so the WGSL numbering does not match the
/// desktop backends', and this model is the single source of truth for it.
/// <para>
/// The ordered <c>…Bindings</c> arrays map Loom's category slots (sampled input <c>k</c>, storage buffer <c>k</c>,
/// uniform buffer <c>k</c>) to the WGSL binding numbers, so a bind group can be filled from the neutral inputs.
/// The push block is identified as the one uniform binding present in the WGSL but absent from reflection's
/// descriptor-bound uniform list, and is bound explicitly by the backend (never counted as a uniform slot).
/// </para>
/// </summary>
internal sealed partial class WebGpuBindingModel
{
	/// <summary>Every bind-group-layout entry, ascending by binding (group 0 only — every Loom shader binds group 0).</summary>
	public required IReadOnlyList<WebGpuLayoutEntry> Entries { get; init; }

	/// <summary>WGSL texture binding of each sampled input, in Loom slot order; paired 1:1 with <see cref="SampledSamplerBindings"/>.</summary>
	public required int[] SampledTextureBindings { get; init; }

	/// <summary>WGSL sampler binding paired with each sampled texture (Slang's combined-sampler split).</summary>
	public required int[] SampledSamplerBindings { get; init; }

	/// <summary>Whether each sampled input's sampler is a <c>sampler_comparison</c> (a shadow sampler read with
	/// <c>SampleCmp</c>), in Loom slot order: such a slot binds a comparison sampler with the technique's
	/// <c>SampleCompare</c>.</summary>
	public bool[] SampledCompared { get; init; } = [];

	/// <summary>How many sampled inputs are read by comparison.</summary>
	public int ComparedCount => SampledCompared.Count(c => c);

	/// <summary>WGSL binding of each read-write storage image (a compute technique's <c>RWTexture2D</c>), in slot order.</summary>
	public required int[] StorageImageBindings { get; init; }

	/// <summary>WGSL binding of each storage buffer, in slot order.</summary>
	public required int[] StorageBufferBindings { get; init; }

	/// <summary>WGSL binding of each descriptor-bound uniform buffer (the per-frame uniform seam), in slot order.</summary>
	public required int[] UniformBufferBindings { get; init; }

	/// <summary>Each uniform buffer's declared size in bytes, parallel to <see cref="UniformBufferBindings"/>. 0 for a slot whose size the sidecar does not report.</summary>
	public required int[] UniformBufferBytes { get; init; }

	/// <summary>WGSL binding of the lowered push-constant uniform block, or -1 when the technique has no push block.</summary>
	public required int PushBinding { get; init; }

	/// <summary>Byte size of the push block (0 when there is none).</summary>
	public required int PushByteSize { get; init; }

	/// <summary>
	/// Which entry of <see cref="UniformBufferBindings"/> is the reserved per-material parameter block, or null when
	/// the shader declares none. An index into that array rather than a WGSL binding index, so it means the same thing
	/// as the desktop backends' uniform slot.
	/// </summary>
	public required int? MaterialUniformSlot { get; init; }

	/// <summary>The size of that block as WGSL lays it out; 0 when the shader declares none.</summary>
	public required int MaterialParameterBytes { get; init; }

	public bool HasBindings => Entries.Count > 0;

	// GPUShaderStage flags (WebGPU spec values, mirrored by the shim). A graphics technique makes its bindings
	// visible to both vertex and fragment (a vertex-stage UBO — foliage wind — needs vertex visibility, and the
	// broader flag is harmless for the rest); a compute technique binds to the compute stage.
	public const int VisibilityVertex = 0x1;
	public const int VisibilityFragment = 0x2;
	public const int VisibilityCompute = 0x4;

	// Bind-group-layout entry kinds — the int the shim's createBindGroupLayout switches on (mirror BGL_KINDS).
	public const int LayoutUniformBuffer = 0;
	public const int LayoutSampledTexture = 1;
	public const int LayoutFilteringSampler = 2;
	public const int LayoutStorageBufferReadOnly = 3;
	public const int LayoutStorageBufferReadWrite = 4;
	public const int LayoutStorageTexture = 5;
	public const int LayoutComparisonSampler = 6;
	public const int LayoutDepthTexture = 7;
	public const int LayoutUnfilterableTexture = 8;
	public const int LayoutNonFilteringSampler = 9;

	/// <summary>
	/// Flatten the layout into the <c>(binding, visibility, kind, extra)</c> int quads the shim's
	/// <c>createBindGroupLayout</c> consumes. Visibility is the whole graphics or compute stage set; the storage
	/// texture's extra carries its format id (for the shim's <c>access</c>/<c>format</c>), zero otherwise.
	/// <para>
	/// When <paramref name="linearFilter"/> is false the technique samples with a nearest sampler, so its sampled
	/// textures bind as <c>unfilterable-float</c> and their sampler as <c>non-filtering</c>. That sample type is a
	/// superset — it accepts a depth-format texture (a <c>depth32float</c> shadow map has no filterable-float sample
	/// type, only depth/unfilterable-float) as well as an ordinary colour texture — and nearest sampling is identical
	/// either way, so it never changes a pixel while letting a nearest technique read a depth map as a plain texture.
	/// A linear-filtering technique keeps the filterable <c>float</c> type (and never samples a depth texture).
	/// </para>
	/// </summary>
	public int[] LayoutEntryData(bool isCompute, bool linearFilter)
	{
		var visibility = isCompute ? VisibilityCompute : VisibilityVertex | VisibilityFragment;
		var data = new int[Entries.Count * 4];
		var i = 0;
		foreach (var entry in Entries)
		{
			data[i++] = entry.Binding;
			data[i++] = visibility;
			data[i++] = entry.Kind switch
			{
				WebGpuBindingKind.UniformBuffer => LayoutUniformBuffer,
				WebGpuBindingKind.Texture => linearFilter ? LayoutSampledTexture : LayoutUnfilterableTexture,
				WebGpuBindingKind.DepthTexture => LayoutDepthTexture,
				WebGpuBindingKind.Sampler => linearFilter ? LayoutFilteringSampler : LayoutNonFilteringSampler,
				WebGpuBindingKind.ComparisonSampler => LayoutComparisonSampler,
				WebGpuBindingKind.StorageBufferReadOnly => LayoutStorageBufferReadOnly,
				WebGpuBindingKind.StorageBufferReadWrite => LayoutStorageBufferReadWrite,
				WebGpuBindingKind.StorageTexture => LayoutStorageTexture,
				_ => throw new WebGpuException($"Unhandled binding kind {entry.Kind}."),
			};
			data[i++] = entry.Extra;
		}
		return data;
	}

	// The scalar push-constant channel is at most sixteen floats on the desktop backends; its WGSL uniform block
	// mirrors that. The exact struct size could be parsed from the WGSL, but the block is always written padded to
	// this size, so a fixed size keeps the bound range defined regardless of how many floats a pass actually pushes.
	private const int PushBlockSize = 16 * sizeof(float);

	// var<class> declarations: `@binding(0) @group(0) var<uniform> name : Type;` and the attribute-less texture /
	// sampler form `@binding(1) @group(0) var name : texture_2d<f32>;`. Attributes are matched as a leading block so
	// the parse is order-independent (@binding before @group or vice-versa). Source-generated (not runtime-compiled):
	// the matcher is emitted at build time, so it works under WASM AOT, which has no dynamic-code support.
	[GeneratedRegex(@"((?:@\w+\([^)]*\)\s*)+)var(?:<(?<class>[^>]*)>)?\s+(?<name>\w+)\s*:\s*(?<type>[^;{]+);")]
	private static partial Regex DeclRegex();

	[GeneratedRegex(@"@binding\(\s*(?<n>\d+)\s*\)")]
	private static partial Regex BindingAttr();

	[GeneratedRegex(@"@group\(\s*(?<n>\d+)\s*\)")]
	private static partial Regex GroupAttr();

	/// <summary>Parse a technique's WGSL module and its reflection sidecar into a binding model.</summary>
	public static WebGpuBindingModel Parse(string wgsl, byte[]? reflectionJson, string name)
	{
		// 1. The WGSL text: the authoritative set of (binding → kind) declarations Slang emitted for group 0.
		var declared = new SortedDictionary<int, WgslDecl>();
		foreach (Match m in DeclRegex().Matches(wgsl))
		{
			var attrs = m.Groups[1].Value;
			var groupMatch = GroupAttr().Match(attrs);
			var bindingMatch = BindingAttr().Match(attrs);
			if (!groupMatch.Success || !bindingMatch.Success || int.Parse(groupMatch.Groups["n"].Value) != 0)
				continue; // a non-group-0 binding (none today) or a plain module-scope var — not a bound resource
			var binding = int.Parse(bindingMatch.Groups["n"].Value);
			declared[binding] = new WgslDecl(m.Groups["class"].Value.Trim(), m.Groups["type"].Value.Trim());
		}

		// 2. The reflection sidecar: Loom slot order (source order) plus each slot's WGSL binding index.
		List<int> sampledTex = [], sampledSamp = [], storageImages = [], storageBuffers = [], uniforms = [], uniformBytes = [];
		var material = (Slot: (int?)null, Bytes: 0);
		if (reflectionJson is not null)
			material = ReadReflection(reflectionJson, sampledTex, sampledSamp, storageImages, storageBuffers, uniforms, uniformBytes);

		// 3. The push block: a WGSL uniform binding that reflection did not attribute to a descriptor-bound uniform.
		var pushBinding = -1;
		foreach (var (binding, decl) in declared)
			if (decl.Class.StartsWith("uniform", StringComparison.Ordinal) && !uniforms.Contains(binding))
			{
				pushBinding = binding;
				break;
			}

		// 4. The layout entries, straight from the WGSL declarations.
		var entries = new List<WebGpuLayoutEntry>(declared.Count);
		foreach (var (binding, decl) in declared)
			entries.Add(new WebGpuLayoutEntry(binding, decl.ClassifyKind(name), decl.StorageTextureFormatId()));

		return new WebGpuBindingModel
		{
			Entries = entries,
			SampledTextureBindings = [.. sampledTex],
			SampledSamplerBindings = [.. sampledSamp],
			SampledCompared = [.. sampledSamp.Select(b => declared.TryGetValue(b, out var d) && d.Type == "sampler_comparison")],
			StorageImageBindings = [.. storageImages],
			StorageBufferBindings = [.. storageBuffers],
			UniformBufferBindings = [.. uniforms],
			UniformBufferBytes = [.. uniformBytes],
			PushBinding = pushBinding,
			PushByteSize = pushBinding >= 0 ? PushBlockSize : 0,
			MaterialUniformSlot = material.Slot,
			MaterialParameterBytes = material.Bytes,
		};
	}

	// Fills the per-category binding lists — including each uniform buffer's declared size, parallel to `uniforms` —
	// and returns which uniform slot (if any) is the reserved per-material parameter block, plus its laid-out size.
	private static (int? Slot, int Bytes) ReadReflection(byte[] json, List<int> sampledTex, List<int> sampledSamp, List<int> storageImages, List<int> storageBuffers, List<int> uniforms, List<int> uniformBytes)
	{
		int? materialSlot = null;
		var materialBytes = 0;
		using var doc = JsonDocument.Parse(json);
		if (!doc.RootElement.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
			return (null, 0);

		foreach (var p in parameters.EnumerateArray())
		{
			if (!p.TryGetProperty("binding", out var binding)
				|| !binding.TryGetProperty("kind", out var bindingKind)
				|| bindingKind.GetString() != "descriptorTableSlot"
				|| !binding.TryGetProperty("index", out var indexElem))
				continue; // push constants and varyings reach the shader by other means
			var index = indexElem.GetInt32();

			if (!p.TryGetProperty("type", out var type) || !type.TryGetProperty("kind", out var typeKind))
				continue;
			var kind = typeKind.GetString();

			if (kind == "constantBuffer")
			{
				var bytes = type.TryGetProperty("elementVarLayout", out var evl)
					&& evl.TryGetProperty("binding", out var evlBinding)
					&& evlBinding.TryGetProperty("size", out var evlSize)
					? evlSize.GetInt32()
					: 0;
				if (p.TryGetProperty("name", out var cbName) && cbName.GetString() == ShaderReflection.MaterialParameterName)
				{
					materialSlot = uniforms.Count; // the slot is this block's ordinal among the uniform buffers
					materialBytes = bytes;
				}
				uniforms.Add(index);
				uniformBytes.Add(bytes);
				continue;
			}
			if (kind != "resource")
				continue;

			var baseShape = type.TryGetProperty("baseShape", out var shape) ? shape.GetString() : null;
			var readWrite = type.TryGetProperty("access", out var access) && access.GetString() == "readWrite";

			if (baseShape is "structuredBuffer" or "byteAddressBuffer")
			{
				storageBuffers.Add(index);
			}
			else if (readWrite)
			{
				storageImages.Add(index); // RWTexture* storage image
			}
			else
			{
				// A combined Sampler2D: Slang splits it into a texture at `index` and a sampler at `index`+1.
				sampledTex.Add(index);
				sampledSamp.Add(index + 1);
			}
		}

		return (materialSlot, materialBytes);
	}

	private readonly record struct WgslDecl(string Class, string Type)
	{
		public WebGpuBindingKind ClassifyKind(string shaderName)
		{
			if (Class.StartsWith("uniform", StringComparison.Ordinal))
				return WebGpuBindingKind.UniformBuffer;
			if (Class.StartsWith("storage", StringComparison.Ordinal))
				return Class.Contains("read_write", StringComparison.Ordinal)
					? WebGpuBindingKind.StorageBufferReadWrite
					: WebGpuBindingKind.StorageBufferReadOnly;
			if (Type.StartsWith("texture_storage_", StringComparison.Ordinal))
				return WebGpuBindingKind.StorageTexture;
			if (Type.StartsWith("texture_depth_", StringComparison.Ordinal))
				return WebGpuBindingKind.DepthTexture;
			if (Type.StartsWith("texture_", StringComparison.Ordinal))
				return WebGpuBindingKind.Texture;
			if (Type == "sampler")
				return WebGpuBindingKind.Sampler;
			if (Type == "sampler_comparison")
				return WebGpuBindingKind.ComparisonSampler;
			throw new WebGpuException($"Shader '{shaderName}': unrecognised WGSL binding type '{Type}' (class '{Class}').");
		}

		// The storage-texture format id (mirrors WebGpuFormatMapping) for a `texture_storage_2d<fmt, access>`; 0 otherwise.
		public int StorageTextureFormatId()
		{
			if (!Type.StartsWith("texture_storage_", StringComparison.Ordinal))
				return 0;
			var open = Type.IndexOf('<');
			var comma = Type.IndexOf(',');
			if (open < 0 || comma < 0)
				return 0;
			var format = Type[(open + 1)..comma].Trim();
			return format switch
			{
				"r32float" => WebGpuFormatMapping.R32Float,
				"rgba8unorm" => WebGpuFormatMapping.Rgba8Unorm,
				"rgba16float" => WebGpuFormatMapping.Rgba16Float,
				"r32uint" => WebGpuFormatMapping.R32Uint,
				_ => throw new WebGpuException($"Unsupported WGSL storage-texture format '{format}'."),
			};
		}
	}
}
