using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Materials;

namespace Loom.Backend.WebGPU;

/// <summary>
/// A named, format-agnostic WGSL technique, built once at <c>DefineTechnique</c>. It owns the format-independent
/// GPU objects — the shader module, the single (group 0) bind-group layout, and the pipeline layout — which are
/// created once here and shared by every per-target-format render pipeline baked from it (the render pipeline is
/// the only part that depends on the pass's attachment formats, mirroring the Vulkan template split). It also
/// carries the <see cref="WebGpuBindingModel"/> the render context uses to fill a bind group from a pass's inputs,
/// and the fixed-function intent (blend/cull/filter/vertex layout) the pipeline bakes in.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class WebGpuTechniqueTemplate : IDisposable
{
	private readonly WebGpuHandleAllocator _handles;

	public required WebGpuBindingModel Bindings { get; init; }
	public required string VertexEntry { get; init; }
	public required string FragmentEntry { get; init; }
	public required bool HasVertexInput { get; init; }
	public required BlendMode Blend { get; init; }
	public required bool LinearFilter { get; init; }
	public required SamplerAddress Address { get; init; }
	public required bool IsCompute { get; init; }
	public required VertexLayout? Layout { get; init; }
	public required VertexLayout? Instance { get; init; }
	public required CullMode Cull { get; init; }

	/// <summary>The depth comparison baked into the pipeline when the pass has a depth target.</summary>
	public required DepthCompare DepthTest { get; init; }

	/// <summary>Constant + slope-scaled depth offset baked into the pipeline's depth-stencil state, or null for none.</summary>
	public required DepthBias? Bias { get; init; }

	/// <summary>Which uniform slot carries the per-material parameter block, or null when the shader declares none.</summary>
	public required int? MaterialUniformSlot { get; init; }

	/// <summary>Bytes of per-material parameters to stage into the uniform ring per draw; 0 when the shader declares no block.</summary>
	public required int MaterialParameterBytes { get; init; }

	/// <summary>
	/// The WGSL <c>@location</c> Slang assigned to each vertex-input attribute, in the shader's field-declaration
	/// order (which matches the technique's stream attribute order: per-vertex stream then per-instance). Slang's
	/// WGSL target does not always number vertex inputs sequentially — with mixed HLSL semantics (e.g. the overlay's
	/// POSITION/TEXCOORD0/COLOR0/TEXCOORD1) it can permute them — so the pipeline must feed each attribute to the
	/// location the emitted WGSL actually declared, not a running counter. Empty when the parse found no vertex input
	/// (a fullscreen or compute technique), in which case a sequential assignment is used.
	/// </summary>
	public required int[] VertexInputLocations { get; init; }

	/// <summary>The shader-module handle (both entry points).</summary>
	public required int ShaderModule { get; init; }

	/// <summary>The group-0 bind-group-layout handle, or -1 when the technique binds nothing.</summary>
	public required int BindGroupLayout { get; init; }

	/// <summary>The pipeline-layout handle (wraps the bind-group layout, or empty when there are no bindings).</summary>
	public required int PipelineLayout { get; init; }

	/// <summary>Number of sampled inputs the technique reads — the count a pass must supply to <c>FullScreen</c>/<c>Draw</c>.</summary>
	public int SampleCount => Bindings.SampledTextureBindings.Length;

	public WebGpuTechniqueTemplate(WebGpuHandleAllocator handles) => _handles = handles;

	/// <summary>
	/// Resolve a logical shader to its WGSL + reflection, parse the binding model, and create the module +
	/// (group 0) layouts on the JS side. Wrapped in a validation error scope by the caller so a malformed layout
	/// surfaces as a named error rather than a black frame.
	/// </summary>
	public static WebGpuTechniqueTemplate Create(WebGpuHandleAllocator handles, string shaderName, string wgsl, byte[]? reflectionJson, in TechniqueDescriptor descriptor)
	{
		var bindings = WebGpuBindingModel.Parse(wgsl, reflectionJson, shaderName);

		var module = handles.Allocate(WebGpuHandleAllocator.ShaderModules);
		WebGpuInterop.CreateShaderModule(module, wgsl, shaderName);

		var bindGroupLayout = -1;
		var pipelineLayout = handles.Allocate(WebGpuHandleAllocator.PipelineLayouts);
		if (bindings.HasBindings)
		{
			bindGroupLayout = handles.Allocate(WebGpuHandleAllocator.BindGroupLayouts);
			WebGpuInterop.CreateBindGroupLayout(bindGroupLayout, bindings.LayoutEntryData(descriptor.IsCompute, descriptor.LinearFilter));
			Span<int> layouts = [bindGroupLayout];
			WebGpuInterop.CreatePipelineLayout(pipelineLayout, layouts);
		}
		else
		{
			WebGpuInterop.CreatePipelineLayout(pipelineLayout, []);
		}

		// The WGSL entry-point names are authoritative — Slang names them by stage (vertexMain / fragmentMain /
		// computeMain), which can differ from the descriptor's desktop-oriented names (a compute technique's SPIR-V
		// entry may be "main" while its WGSL entry is "computeMain"). Read them from the reflection, falling back to
		// the descriptor. A compute technique's single entry rides FragmentEntry (BakeCompute reads it there).
		var (vertexEntry, fragmentEntry) = ResolveEntryPoints(reflectionJson, descriptor);

		var layout = descriptor.Layout ?? (descriptor.HasVertexInput ? VertexLayout.MeshStandard : null);
		return new WebGpuTechniqueTemplate(handles)
		{
			Bindings = bindings,
			VertexEntry = vertexEntry,
			FragmentEntry = fragmentEntry,
			VertexInputLocations = ParseVertexInputLocations(wgsl, vertexEntry),
			HasVertexInput = descriptor.HasVertexInput,
			Blend = descriptor.Blend,
			LinearFilter = descriptor.LinearFilter,
			Address = descriptor.Address,
			IsCompute = descriptor.IsCompute,
			Layout = layout,
			Instance = descriptor.Instance,
			Cull = descriptor.Cull,
			DepthTest = descriptor.DepthTest,
			Bias = descriptor.Bias,
			MaterialUniformSlot = bindings.MaterialUniformSlot,
			MaterialParameterBytes = bindings.MaterialParameterBytes,
			ShaderModule = module,
			BindGroupLayout = bindGroupLayout,
			PipelineLayout = pipelineLayout,
		};
	}

	// Read the emitted WGSL entry-point names by stage from the reflection sidecar. Returns (vertexEntry,
	// fragmentEntry); for a compute technique the compute entry is returned as fragmentEntry (BakeCompute's slot).
	private static (string Vertex, string Fragment) ResolveEntryPoints(byte[]? reflectionJson, in TechniqueDescriptor descriptor)
	{
		var vertex = descriptor.VertexEntry;
		var fragment = descriptor.FragmentEntry;
		if (reflectionJson is null)
			return (vertex, fragment);

		using var doc = JsonDocument.Parse(reflectionJson);
		if (!doc.RootElement.TryGetProperty("entryPoints", out var entries) || entries.ValueKind != JsonValueKind.Array)
			return (vertex, fragment);

		foreach (var entry in entries.EnumerateArray())
		{
			if (!entry.TryGetProperty("name", out var nameElem) || !entry.TryGetProperty("stage", out var stageElem))
				continue;
			var name = nameElem.GetString();
			if (name is null)
				continue;
			switch (stageElem.GetString())
			{
				case "vertex": vertex = name; break;
				case "fragment": fragment = name; break;
				case "compute": fragment = name; break; // the compute entry rides FragmentEntry
			}
		}
		return (vertex, fragment);
	}

	// Source-generated matchers (emitted at build time, so they run under WASM AOT, which has no dynamic-code support).
	// The function/struct patterns capture the name generically — a WGSL identifier is `\w+`, so a name is matched and
	// then compared by string equality, which also removes the need to escape it into the pattern.
	[GeneratedRegex(@"fn\s+(?<name>\w+)\s*\((?<params>[^)]*)\)")]
	private static partial Regex FunctionSignature();

	[GeneratedRegex(@"struct\s+(?<name>\w+)\s*\{(?<body>[^}]*)\}")]
	private static partial Regex StructDecl();

	[GeneratedRegex(@"@location\(\s*(?<n>\d+)\s*\)\s*\w+\s*:")]
	private static partial Regex LocationField();

	// Parse the vertex entry's input struct(s) and return the @location of each field in declaration order. Slang emits
	// the vertex stage's input as one or more struct-typed parameters on the entry function (plus @builtin scalars,
	// which are skipped); each struct's fields carry the @location attributes assigned for the WGSL target. The fields
	// appear in source order, so the returned list lines up positionally with the technique's stream attributes.
	private static int[] ParseVertexInputLocations(string wgsl, string vertexEntry)
	{
		Match? entry = null;
		foreach (Match fn in FunctionSignature().Matches(wgsl))
			if (fn.Groups["name"].Value == vertexEntry)
			{
				entry = fn;
				break;
			}
		if (entry is null)
			return [];

		// Index every struct body by name so a parameter's declared type resolves without re-scanning the module.
		var structBodies = new Dictionary<string, string>();
		foreach (Match s in StructDecl().Matches(wgsl))
			structBodies[s.Groups["name"].Value] = s.Groups["body"].Value;

		var locations = new List<int>();
		foreach (var param in entry.Groups["params"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
		{
			var colon = param.LastIndexOf(':');
			if (colon < 0)
				continue;
			var typeName = param[(colon + 1)..].Trim();
			// A struct parameter — its fields are the vertex inputs. A @builtin scalar (vertex_index) types to a
			// scalar with no matching struct declaration, so it contributes nothing.
			if (!structBodies.TryGetValue(typeName, out var body))
				continue;
			foreach (Match f in LocationField().Matches(body))
				locations.Add(int.Parse(f.Groups["n"].Value));
		}
		return [.. locations];
	}

	public void Dispose()
	{
		_handles.Release(WebGpuHandleAllocator.PipelineLayouts, PipelineLayout);
		if (BindGroupLayout >= 0)
			_handles.Release(WebGpuHandleAllocator.BindGroupLayouts, BindGroupLayout);
		_handles.Release(WebGpuHandleAllocator.ShaderModules, ShaderModule);
	}
}
