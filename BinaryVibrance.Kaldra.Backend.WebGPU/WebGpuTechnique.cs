using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

using BinaryVibrance.Kaldra.Core.Geometry;
using BinaryVibrance.Kaldra.Core.Graph;

namespace BinaryVibrance.Kaldra.Backend.WebGPU;

/// <summary>
/// A baked WGSL render pipeline — one <see cref="WebGpuTechniqueTemplate"/> specialised to a pass's attachment
/// formats (colour target formats + optional depth), mirroring the Vulkan backend's per-format pipeline cache. The
/// pipeline is created from a JSON descriptor: pipeline creation is a rare bake-time operation, so a debuggable
/// JSON crossing beats a compact binary one, and every module/layout reference inside it is an integer handle.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class WebGpuTechnique : IDisposable
{
	private readonly WebGpuHandleAllocator _handles;

	public required int Pipeline { get; init; }
	public required WebGpuTechniqueTemplate Template { get; init; }

	/// <summary>True for a compute pipeline (its handle lives in the compute-pipeline table, dispatched not drawn).</summary>
	public required bool IsCompute { get; init; }

	private WebGpuTechnique(WebGpuHandleAllocator handles) => _handles = handles;

	/// <summary>Bake the render pipeline for a technique against a pass's attachment formats.</summary>
	public static WebGpuTechnique Bake(WebGpuHandleAllocator handles, WebGpuTechniqueTemplate template, string name, ReadOnlySpan<int> colorFormatIds, int depthFormatId, bool depthWrite, int samples = 1)
	{
		var pipeline = handles.Allocate(WebGpuHandleAllocator.RenderPipelines);
		var json = BuildDescriptor(template, name, colorFormatIds, depthFormatId, depthWrite, samples);
		WebGpuInterop.CreateRenderPipeline(pipeline, json);
		return new WebGpuTechnique(handles) { Pipeline = pipeline, Template = template, IsCompute = false };
	}

	/// <summary>Bake the compute pipeline for a compute technique (no attachment formats — its single entry point).</summary>
	public static WebGpuTechnique BakeCompute(WebGpuHandleAllocator handles, WebGpuTechniqueTemplate template, string name)
	{
		var pipeline = handles.Allocate(WebGpuHandleAllocator.ComputePipelines);
		WebGpuInterop.CreateComputePipeline(pipeline, template.PipelineLayout, template.ShaderModule, template.FragmentEntry, name);
		return new WebGpuTechnique(handles) { Pipeline = pipeline, Template = template, IsCompute = true };
	}

	// depthFormatId < 0 means the pass has no depth attachment.
	private static string BuildDescriptor(WebGpuTechniqueTemplate template, string name, ReadOnlySpan<int> colorFormatIds, int depthFormatId, bool depthWrite, int samples)
	{
		var sb = new StringBuilder(512);
		sb.Append('{');
		sb.Append("\"label\":\"").Append(name).Append("\",");
		sb.Append("\"layout\":").Append(template.PipelineLayout).Append(',');
		sb.Append("\"module\":").Append(template.ShaderModule).Append(',');
		sb.Append("\"vertexEntry\":\"").Append(template.VertexEntry).Append("\",");
		sb.Append("\"fragmentEntry\":\"").Append(template.FragmentEntry).Append("\",");
		sb.Append("\"topology\":\"triangle-list\",");
		sb.Append("\"frontFace\":\"ccw\",");
		sb.Append("\"cullMode\":\"").Append(CullString(template.Cull)).Append("\",");

		sb.Append("\"vertexBuffers\":");
		AppendVertexBuffers(sb, template);
		sb.Append(',');

		sb.Append("\"colorTargets\":[");
		for (var i = 0; i < colorFormatIds.Length; i++)
		{
			if (i > 0)
				sb.Append(',');
			sb.Append("{\"format\":").Append(colorFormatIds[i]).Append(",\"blend\":");
			AppendBlend(sb, template.Blend);
			sb.Append('}');
		}
		sb.Append("],");

		sb.Append("\"depth\":");
		if (depthFormatId >= 0)
		{
			var bias = template.Bias ?? default;
			sb.Append("{\"format\":").Append(depthFormatId)
				.Append(",\"write\":").Append(depthWrite ? "true" : "false")
				.Append(",\"compare\":\"").Append(CompareString(template.DepthTest))
				.Append("\",\"bias\":").Append(bias.Constant)
				.Append(",\"biasSlopeScale\":").Append(bias.SlopeScale.ToString(CultureInfo.InvariantCulture))
				.Append(",\"biasClamp\":").Append(bias.Clamp.ToString(CultureInfo.InvariantCulture))
				.Append('}');
		}
		else
		{
			sb.Append("null");
		}

		// The pass's multisample count, and whether alpha decides the samples a fragment covers
		sb.Append(",\"multisample\":{\"count\":").Append(samples)
			.Append(",\"alphaToCoverage\":").Append(template.AlphaToCoverage && samples > 1 ? "true" : "false").Append('}');

		sb.Append('}');
		return sb.ToString();
	}

	// Vertex input built from the technique's declared streams: an optional per-vertex layout and an optional
	// per-instance layout. WebGPU shader locations are one flat namespace across buffers, so attributes get
	// sequential locations in stream order (per-vertex first, then per-instance) — matching the WGSL @location(n).
	private static void AppendVertexBuffers(StringBuilder sb, WebGpuTechniqueTemplate template)
	{
		// The shader location each attribute feeds is the one Slang emitted for it (parsed from the WGSL vertex input),
		// not a running counter: Slang's WGSL target can permute vertex-input locations relative to the stream order.
		// The parsed list lines up positionally with the streams (per-vertex then per-instance); fall back to a
		// sequential assignment when it is unavailable or its count disagrees (fullscreen/compute have no inputs).
		var wgslLocations = template.VertexInputLocations;
		var totalAttributes = (template.Layout?.Attributes.Length ?? 0) + (template.Instance?.Attributes.Length ?? 0);
		var useParsed = wgslLocations.Length == totalAttributes;

		sb.Append('[');
		var attribute = 0;
		var first = true;
		foreach (var stream in new[] { template.Layout, template.Instance })
		{
			if (stream is null)
				continue;
			if (!first)
				sb.Append(',');
			first = false;
			sb.Append("{\"arrayStride\":").Append(stream.Stride)
				.Append(",\"stepMode\":\"").Append(stream.InputRate == VertexInputRate.PerInstance ? "instance" : "vertex")
				.Append("\",\"attributes\":[");
			for (var i = 0; i < stream.Attributes.Length; i++)
			{
				if (i > 0)
					sb.Append(',');
				var location = useParsed ? wgslLocations[attribute] : attribute;
				attribute++;
				sb.Append("{\"shaderLocation\":").Append(location)
					.Append(",\"offset\":").Append(stream.Offsets[i])
					.Append(",\"format\":\"").Append(VertexFormatString(stream.Attributes[i].Format)).Append("\"}");
			}
			sb.Append("]}");
		}
		sb.Append(']');
	}

	// A blend object (WebGPU GPUBlendState) or null for opaque replace. The equations mirror the Vulkan backend's
	// blend-factor choices so a technique blends identically across backends.
	private static void AppendBlend(StringBuilder sb, BlendMode blend)
	{
		if (blend == BlendMode.None)
		{
			sb.Append("null");
			return;
		}
		var (src, dst) = blend switch
		{
			BlendMode.AlphaOver => ("src-alpha", "one-minus-src-alpha"),
			BlendMode.PremultipliedAlpha => ("one", "one-minus-src-alpha"),
			BlendMode.Additive => ("one", "one"),
			BlendMode.Multiply => ("dst", "zero"),
			_ => throw new WebGpuException($"No WebGPU blend mapping for {blend}."),
		};
		sb.Append("{\"color\":{\"srcFactor\":\"").Append(src).Append("\",\"dstFactor\":\"").Append(dst).Append("\",\"operation\":\"add\"},")
			.Append("\"alpha\":{\"srcFactor\":\"").Append(src).Append("\",\"dstFactor\":\"").Append(dst).Append("\",\"operation\":\"add\"}}");
	}

	// Exhaustive so a new DepthCompare member fails loud here instead of emitting a string WebGPU rejects at
	// pipeline creation, far from the technique that caused it.
	private static string CompareString(DepthCompare compare) => compare switch
	{
		DepthCompare.Never => "never",
		DepthCompare.Less => "less",
		DepthCompare.Equal => "equal",
		DepthCompare.LessOrEqual => "less-equal",
		DepthCompare.Greater => "greater",
		DepthCompare.NotEqual => "not-equal",
		DepthCompare.GreaterOrEqual => "greater-equal",
		DepthCompare.Always => "always",
		_ => throw new NotSupportedException($"WebGPU: DepthCompare '{compare}' is not mapped to a compare string."),
	};

	private static string CullString(CullMode cull) => cull switch
	{
		CullMode.None => "none",
		CullMode.Back => "back",
		CullMode.Front => "front",
		_ => throw new WebGpuException($"No WebGPU cull mapping for {cull}."),
	};

	private static string VertexFormatString(VertexFormat format) => format switch
	{
		VertexFormat.Float => "float32",
		VertexFormat.Float2 => "float32x2",
		VertexFormat.Float3 => "float32x3",
		VertexFormat.Float4 => "float32x4",
		VertexFormat.UInt4 => "uint32x4",
		VertexFormat.Half4 => "float16x4",
		VertexFormat.UNorm4 => "unorm8x4",
		_ => throw new WebGpuException($"No WebGPU vertex format mapping for {format}."),
	};

	public void Dispose() => _handles.Release(IsCompute ? WebGpuHandleAllocator.ComputePipelines : WebGpuHandleAllocator.RenderPipelines, Pipeline);
}
