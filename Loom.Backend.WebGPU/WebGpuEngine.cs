using System.Runtime.Versioning;

using Loom.Core.Animation;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Materials;
using Loom.Graph;

namespace Loom.Backend.WebGPU;

/// <summary>
/// The browser WebGPU engine — the <see cref="EngineBase"/> implementation that turns the backend-neutral
/// graph plans into WebGPU work issued through the JS shim. It deliberately has no barriers partial: WebGPU
/// tracks resource state itself, so the Execute layer's transitions are not emitted (only the attachment
/// load/store actions survive, into the render-pass descriptors). It has no swapchain type either — the canvas
/// context is configured on the JS side; the present target is just the canvas's current texture view.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class WebGpuEngine : EngineBase
{
	private readonly WebGpuSurface _surface;
	private readonly WebGpuEngineSettings _settings;
	private readonly WebGpuHandleAllocator _handles = new();
	private readonly WebGpuCommandWriter _writer = new();
	private readonly WebGpuUploadArena _arena = new();
	private readonly WebGpuSamplerCache _samplers;
	private readonly WebGpuMeshStore _meshes;
	private readonly WebGpuTextureStore _textures;
	private readonly MaterialStore _materials = new();
	private readonly WebGpuBufferRing _uniformRing;
	private readonly WebGpuBufferRing _instanceRing;
	private readonly WebGpuBufferRing _storageRing;
	private readonly WebGpuRenderContext _context;
	// Logical technique name → its format-agnostic baked inputs (shader module + layouts + binding model). Built at
	// DefineTechnique, disposed on hot-reload/teardown; the per-attachment-format render pipelines are cached and
	// baked lazily by the render context (WebGpuRenderContext.Bake).
	private readonly Dictionary<string, WebGpuTechniqueTemplate> _templates = [];

	private WebGpuTransientResources? _transients;
	private int _width;
	private int _height;
	private float _elapsed;

	public WebGpuEngine(WebGpuSurface surface, int width, int height, WebGpuEngineSettings settings)
	{
		_surface = surface;
		_settings = settings;
		_width = Math.Max(1, width);
		_height = Math.Max(1, height);
		_samplers = new WebGpuSamplerCache(_handles);
		_meshes = new WebGpuMeshStore(_handles);
		_textures = new WebGpuTextureStore(_handles);
		_uniformRing = new WebGpuBufferRing(_handles, _arena, _writer, WebGpuStateMapping.BufferUniform, surface.MinUniformBufferOffsetAlignment, "uniform-ring");
		_instanceRing = new WebGpuBufferRing(_handles, _arena, _writer, WebGpuStateMapping.BufferVertex, minAlignment: 4, "instance-ring");
		// Feeds the pre-skin compute pass its per-frame bone palette. 256-byte alignment is WebGPU's default
		// minStorageBufferOffsetAlignment ceiling, so a ring offset is always a valid storage-buffer binding offset.
		_storageRing = new WebGpuBufferRing(_handles, _arena, _writer, WebGpuStateMapping.BufferStorage, minAlignment: 256, "storage-ring");
		_context = new WebGpuRenderContext(this);
		WebGpuInterop.ConfigureCanvas(_width, _height);
	}

	// WebGPU normalised-device Y points up (like DirectX12/Metal, unlike Vulkan's Y-down). The desktop backends
	// negate clip-space Y and DirectX12 additionally flips the viewport to compensate; WebGPU has no negative-height
	// viewport, so it takes the plain identity — its Y-up NDC already puts app-space up at the top of the screen.
	public override System.Numerics.Matrix4x4 ClipSpaceCorrection => System.Numerics.Matrix4x4.Identity;

	// Collaborators the per-frame render context reaches through the engine (all created here and stable, except
	// the transients, which the engine rebinds on each graph swap).
	internal WebGpuCommandWriter Writer => _writer;
	internal WebGpuUploadArena Arena => _arena;
	internal WebGpuHandleAllocator Handles => _handles;
	internal WebGpuSamplerCache Samplers => _samplers;
	internal WebGpuMeshStore Meshes => _meshes;
	internal WebGpuBufferRing UniformRing => _uniformRing;
	internal WebGpuBufferRing InstanceRing => _instanceRing;
	internal WebGpuBufferRing StorageRing => _storageRing;
	internal WebGpuTransientResources Transients => _transients ?? throw new WebGpuException("No active graph — SetGraph must run before a pass records.");

	/// <summary>The texture ids (into the texture store) a material bundles, in bind order.</summary>
	internal ReadOnlySpan<int> MaterialTextures(int materialId) => _materials.TexturesOf(materialId);

	/// <summary>The sampled view handle of an uploaded texture.</summary>
	internal int TextureView(int textureId) => _textures.View(textureId);

	/// <summary>Look up a registered technique's format-agnostic template.</summary>
	internal WebGpuTechniqueTemplate Template(string name) =>
		_templates.TryGetValue(name, out var template) ? template : throw new WebGpuException($"No technique registered under the name '{name}'.");

	public override string BackendName => "WebGPU";
	public override string DeviceName => _surface.AdapterName;

	// The engine's own WGSL shaders are embedded under "<assembly>.Shaders." (the build's LoomEmbedManifestPrefix).
	private static readonly string EmbeddedShaderPrefix = typeof(WebGpuEngine).Assembly.GetName().Name + ".Shaders.";

	// Timestamp queries are an optional WebGPU feature and unavailable on many browsers/adapters; timing lands
	// once the feature is negotiated at device request.
	protected override bool CanEnableGpuTiming => false;

	// Build the technique's format-agnostic inputs: resolve the logical shader to its WGSL module + reflection,
	// parse the binding model, and create the shader module + group-0 layouts on the JS side. The render pipeline
	// itself is baked lazily per attachment-format set (WebGpuRenderContext.Bake). Wrapped in a validation error
	// scope so a malformed layout surfaces as a named error rather than a silently broken pipeline.
	protected override void BakeTechnique(string name, TechniqueDescriptor descriptor)
	{
		if (_templates.Remove(name, out var previous))
			previous.Dispose();

		var wgsl = ResolveShaderText(descriptor.Shader);
		var reflection = ResolveShaderAsset(descriptor.Shader, ".wgsl.json");

		WebGpuInterop.PushErrorScope();
		var template = WebGpuTechniqueTemplate.Create(_handles, descriptor.Shader, wgsl, reflection, descriptor);
		WebGpuInterop.PopErrorScope($"bake technique '{name}'");
		var errors = WebGpuInterop.TakeErrors();
		if (!string.IsNullOrEmpty(errors))
			Console.WriteLine($"[loom][webgpu] {errors}");

		_templates[name] = template;
	}

	protected override void ResetPipelineCaches()
	{
		_context.ClearBaked();
		foreach (var template in _templates.Values)
			template.Dispose();
		_templates.Clear();
	}

	// Resolve a logical shader name to its WGSL module text, decoded UTF-8 (the ".wgsl" asset is required, so
	// ResolveShaderAsset throws rather than returning null for it).
	private string ResolveShaderText(string name) =>
		System.Text.Encoding.UTF8.GetString(ResolveShaderAsset(name, ".wgsl")!);

	// Resolve a logical shader name + artifact extension (".wgsl" or its ".wgsl.json" reflection sidecar) to bytes.
	// The engine's own infrastructure shaders are embedded in this assembly; app (demo) shaders resolve through the
	// host-supplied ShaderResolver (a browser has no shader directory to fall back to). The reflection sidecar is
	// optional — a shader with no bound resources needs none — so ".wgsl.json" may resolve to null.
	private byte[]? ResolveShaderAsset(string name, string extension)
	{
		var assembly = typeof(WebGpuEngine).Assembly;
		using var embedded = assembly.GetManifestResourceStream(EmbeddedShaderPrefix + name + extension);
		if (embedded is not null)
		{
			var bytes = new byte[embedded.Length];
			embedded.ReadExactly(bytes);
			return bytes;
		}

		var resolved = _settings.ShaderResolver?.Invoke(name + extension);
		if (resolved is not null)
			return resolved;

		if (extension == ".wgsl")
			throw new WebGpuException(
				$"Shader '{name}{extension}' not found: neither embedded ('{EmbeddedShaderPrefix}{name}{extension}') nor resolved by the host ShaderResolver.");
		return null; // an optional reflection sidecar
	}

	protected override void ReleaseGraphResources()
	{
		// The bind groups reference this graph's transient views; those handles are recycled when the transients are
		// disposed, so drop the cache first or a later graph could bind a group onto the wrong (reused-handle) texture.
		_context.ClearBindGroups();
		_transients?.Dispose();
		_transients = null;
	}

	protected override void RebuildGraphResources(RenderGraph authored, RenderGraph executed) =>
		_transients = new WebGpuTransientResources(_handles, executed, Barriers, _surface.BackbufferFormat, _width, _height);

	protected override void GrowStorage(int id, int newByteSize) => _transients!.GrowStorage(id, newByteSize);

	public override void Resize(int width, int height)
	{
		_width = Math.Max(1, width);
		_height = Math.Max(1, height);
		WebGpuInterop.ConfigureCanvas(_width, _height);
		_transients?.Resize(_width, _height);
	}

	// WebGPU has no host-side GPU idle wait, and it defers destruction of in-flight resources internally, so
	// the release-after-drain boundaries EngineBase sequences around (SetGraph, GrowStorage, Dispose) are safe
	// without an explicit wait.
	public override void WaitIdle() { }

	public override Mesh CreateMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices) => _meshes.Create(vertices, indices);

	public override Mesh CreateMesh<T>(ReadOnlySpan<T> vertices, VertexLayout layout, ReadOnlySpan<uint> indices)
	{
		var stride = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
		if (stride != layout.Stride)
			throw new WebGpuException($"Vertex element size ({stride} bytes) does not match the layout stride ({layout.Stride} bytes).");
		return _meshes.Create(System.Runtime.InteropServices.MemoryMarshal.AsBytes(vertices), stride, indices);
	}

	public override Mesh CreateSkinnedMesh(ReadOnlySpan<SkinnedVertex> vertices, ReadOnlySpan<uint> indices) => _meshes.CreateSkinned(vertices, indices);

	public override Texture CreateTexture(TextureData data) => _textures.Create(data);

	public override Material CreateMaterial(params ReadOnlySpan<Texture> textures) => _materials.Create(textures);

	public override void Dispose()
	{
		_transients?.Dispose();
		_transients = null;
		_context.Dispose();
		foreach (var template in _templates.Values)
			template.Dispose();
		_templates.Clear();
		_samplers.Dispose();
		_meshes.Dispose();
		_textures.Dispose();
		if (_captureBuffer >= 0)
			_handles.Release(WebGpuHandleAllocator.Buffers, _captureBuffer);
	}
}
