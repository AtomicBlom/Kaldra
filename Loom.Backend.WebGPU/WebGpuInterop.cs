using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Loom.Backend.WebGPU;

/// <summary>
/// The complete [JSImport] surface into <c>loom-webgpu.js</c> — the backend's marshaling seam. Object creation
/// and other low-frequency operations are individual calls; per-frame work crosses exactly once, via
/// <see cref="SubmitFrame"/> with the binary command stream (<see cref="WebGpuCommandWriter"/>) and the frame's
/// upload arena. Spans marshal as MemoryViews over WASM linear memory, valid for the duration of the call.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class WebGpuInterop
{
	private const string Module = "loom-webgpu";

	/// <summary>The command-stream protocol version (see <see cref="WebGpuProtocol"/>).</summary>
	public const int ProtocolVersion = WebGpuProtocol.Version;

	// ── Device / canvas (async init happens in the HOST before Engine.Create; these are the sync remainder) ──

	[JSImport("configureCanvas", Module)]
	public static partial void ConfigureCanvas(int width, int height);

	// ── Object creation (bake/startup time; individual calls, C#-allocated handles) ──

	[JSImport("createBuffer", Module)]
	public static partial void CreateBuffer(int handle, int byteSize, int usage, string label);

	[JSImport("createTexture", Module)]
	public static partial void CreateTexture(int handle, int width, int height, int mipCount, int formatId, int usage, int sampleCount, string label);

	[JSImport("createTextureView", Module)]
	public static partial void CreateTextureView(int handle, int textureHandle, int formatId, int baseMip, int mipCount, string label);

	[JSImport("createSampler", Module)]
	public static partial void CreateSampler(int handle, bool linear, int addressMode, int compare);

	[JSImport("createShaderModule", Module)]
	public static partial void CreateShaderModule(int handle, string wgsl, string label);

	/// <summary>Entries: repeating records of (binding, visibility, kind, extra) int quads — see BGL_KINDS in the shim.</summary>
	[JSImport("createBindGroupLayout", Module)]
	public static partial void CreateBindGroupLayout(int handle, [JSMarshalAs<JSType.MemoryView>] Span<int> entries);

	[JSImport("createPipelineLayout", Module)]
	public static partial void CreatePipelineLayout(int handle, [JSMarshalAs<JSType.MemoryView>] Span<int> bindGroupLayoutHandles);

	/// <summary>Render-pipeline creation takes the descriptor as JSON — bake-time only, so marshaling simplicity
	/// and debuggability beat compactness. Module/layout references inside the JSON are integer handles.</summary>
	[JSImport("createRenderPipeline", Module)]
	public static partial void CreateRenderPipeline(int handle, string descriptorJson);

	[JSImport("createComputePipeline", Module)]
	public static partial void CreateComputePipeline(int handle, int layoutHandle, int moduleHandle, string entryPoint, string label);

	/// <summary>Entries: repeating records of (binding, kind, resourceHandle, offset, size) — kind 0 = buffer,
	/// 1 = texture view, 2 = sampler. Offset/size are buffer-only (bytes; size 0 = whole buffer).</summary>
	[JSImport("createBindGroup", Module)]
	public static partial void CreateBindGroup(int handle, int layoutHandle, [JSMarshalAs<JSType.MemoryView>] Span<int> entries);

	// ── Immediate data upload (startup/resource creation; per-frame data goes through the arena instead) ──

	[JSImport("writeBuffer", Module)]
	public static partial void WriteBuffer(int bufferHandle, int dstOffset, [JSMarshalAs<JSType.MemoryView>] Span<byte> bytes);

	[JSImport("writeTexture", Module)]
	public static partial void WriteTexture(int textureHandle, int mip, int width, int height, int bytesPerRow, [JSMarshalAs<JSType.MemoryView>] Span<byte> bytes);

	// ── Per-frame ──

	/// <summary>THE per-frame crossing: replay <paramref name="commands"/> (see <see cref="WebGpuCommandWriter"/>)
	/// into one command encoder and submit, resolving arena references against <paramref name="arena"/>.</summary>
	[JSImport("submitFrame", Module)]
	public static partial void SubmitFrame([JSMarshalAs<JSType.MemoryView>] Span<byte> commands, [JSMarshalAs<JSType.MemoryView>] Span<byte> arena);

	// ── Async readback (mapAsync resolves on a later frame; C# polls) ──

	[JSImport("beginReadbackMap", Module)]
	public static partial void BeginReadbackMap(int slot, int bufferHandle);

	/// <summary>If the slot's mapAsync completed: copy the mapped bytes into <paramref name="destination"/>,
	/// unmap, and return true. False while still in flight.</summary>
	[JSImport("tryFetchReadback", Module)]
	public static partial bool TryFetchReadback(int slot, [JSMarshalAs<JSType.MemoryView>] Span<byte> destination);

	/// <summary>Trigger a browser download of a captured frame's PNG bytes under <paramref name="fileName"/>.</summary>
	[JSImport("downloadCapture", Module)]
	public static partial void DownloadCapture(string fileName, [JSMarshalAs<JSType.MemoryView>] Span<byte> png);

	/// <summary>POST a captured frame's PNG bytes to the harness endpoint (<c>/capture?name=…</c>) for the automated
	/// diff loop; falls back to a download if no server is listening.</summary>
	[JSImport("postCapture", Module)]
	public static partial void PostCapture(string name, [JSMarshalAs<JSType.MemoryView>] Span<byte> png);

	// ── Housekeeping ──

	[JSImport("releaseHandle", Module)]
	public static partial void ReleaseHandle(int table, int handle);

	/// <summary>Drain the shim's accumulated WebGPU error messages (uncaptured errors + popped validation
	/// scopes); empty string when clean. Checked after bake operations and once per frame.</summary>
	[JSImport("takeErrors", Module)]
	public static partial string TakeErrors();

	[JSImport("pushErrorScope", Module)]
	public static partial void PushErrorScope();

	[JSImport("popErrorScope", Module)]
	public static partial void PopErrorScope(string context);
}
