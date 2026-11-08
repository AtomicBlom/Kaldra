using System.Runtime.Versioning;
using Loom.Core.Animation;
using Loom.Core.Diagnostics;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
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
	private readonly WebGpuBufferRing _uniformRing;
	private readonly WebGpuBufferRing _instanceRing;
	private readonly WebGpuBufferRing _storageRing;
	// The dynamic geometry a draw list stages each frame: one ring for its vertices, one for its 16-bit indices.
	// Several draw lists in a frame bump-append at successive offsets rather than overwrite each other.
	private readonly WebGpuBufferRing _listVertexRing;
	private readonly WebGpuBufferRing _listIndexRing;
	// Renderer-private offscreen targets backing UI group-opacity layers (below the graph seam, so the graph still
	// sees one ui pass). Sized/formatted to a pass on demand when a draw list carries layer markers.
	private readonly WebGpuLayerTargets _layerTargets;
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
		: base(settings)
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
		_listVertexRing = new WebGpuBufferRing(_handles, _arena, _writer, WebGpuStateMapping.BufferVertex, minAlignment: 4, "ui-vertex-ring");
		_listIndexRing = new WebGpuBufferRing(_handles, _arena, _writer, WebGpuStateMapping.BufferIndex, minAlignment: 4, "ui-index-ring");
		_layerTargets = new WebGpuLayerTargets(_handles);
		_context = new WebGpuRenderContext(this, Validation);
		Capabilities = new EngineCapabilities
		{
			// Timestamp queries are an optional WebGPU feature, unavailable on many browsers/adapters; timing lands
			// once the feature is negotiated at device request.
			GpuTiming = false,
			AsyncCompute = false,
			// WebGPU defines exactly these two: every device supports 1 and 4, and nothing else is expressible.
			SampleCounts = SampleCounts.X1 | SampleCounts.X4,
			MaxColorAttachments = 8,
			MinUniformBufferOffsetAlignment = surface.MinUniformBufferOffsetAlignment,
			Float32Filterable = surface.Float32Filterable,
		};
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
	internal WebGpuBufferRing ListVertexRing => _listVertexRing;
	internal WebGpuBufferRing ListIndexRing => _listIndexRing;
	internal WebGpuLayerTargets LayerTargets => _layerTargets;
	internal WebGpuTransientResources Transients => _transients ?? throw new WebGpuException("No active graph — SetGraph must run before a pass records.");

	/// <summary>The texture ids (into the texture store) a material bundles, in bind order.</summary>
	internal ReadOnlySpan<int> MaterialTextures(int materialId) => Materials.TexturesOf(materialId);

	/// <summary>Check a material against the technique about to draw it, memoized per pair (the shared contract check).</summary>
	internal void ValidateMaterial(Material material, string technique) => Materials.ValidateAgainst(material, technique);

	/// <summary>A material's per-material parameter bytes, to stage into the uniform ring for this draw. Empty when it carries none.</summary>
	internal ReadOnlySpan<byte> MaterialParameters(int materialId) => Materials.ParametersOf(materialId);

	/// <summary>The sampled view handle of an uploaded texture.</summary>
	internal int TextureView(int textureId) => _textures.View(textureId);

	/// <summary>Look up a registered technique's format-agnostic template.</summary>
	internal WebGpuTechniqueTemplate Template(string name) =>
		_templates.TryGetValue(name, out var template) ? template : throw new TechniqueNotDefinedException(name);

	public override string BackendName => "WebGPU";
	public override string DeviceName => _surface.AdapterName;

	// The engine's own WGSL shaders are embedded under "<assembly>.Shaders." (the build's LoomEmbedManifestPrefix).
	private static readonly string EmbeddedShaderPrefix = typeof(WebGpuEngine).Assembly.GetName().Name + ".Shaders.";

	public override EngineCapabilities Capabilities { get; }

	// Build the technique's format-agnostic inputs: resolve the logical shader to its WGSL module + reflection,
	// parse the binding model, and create the shader module + group-0 layouts on the JS side. The render pipeline
	// itself is baked lazily per attachment-format set (WebGpuRenderContext.Bake). Wrapped in a validation error
	// scope so a malformed layout surfaces as a named error rather than a silently broken pipeline.
	protected override TechniqueShape BakeTechnique(string name, TechniqueDescriptor descriptor)
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
			LoomLog.Error($"[webgpu] {errors}");

		_templates[name] = template;
		return new TechniqueShape(
			new MaterialLayout(template.SampleCount, template.MaterialParameterBytes, template.Bindings.ComparedCount),
			[.. template.Bindings.UniformBufferBytes],
			// WGSL has no push constants, so a LOOM_PUSH_CONSTANT block lowers to an ordinary uniform buffer; the
			// binding model reports its size, and 0 when the technique declares none.
			template.Bindings.PushByteSize,
			descriptor.HasListPush ? template.Bindings.PushByteSize : 0);
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
		ReleaseGraphReadbacks();
	}

	protected override void RebuildGraphResources(RenderGraph authored, RenderGraph executed)
	{
		_transients = new WebGpuTransientResources(_handles, executed, Barriers, _surface.BackbufferFormat, _width, _height);
		RebuildGraphReadbacks(authored);
	}

	protected override void GrowStorage(int id, int newByteSize) => _transients!.GrowStorage(id, newByteSize);

	public override void Resize(int width, int height)
	{
		_width = Math.Max(1, width);
		_height = Math.Max(1, height);
		WebGpuInterop.ConfigureCanvas(_width, _height);
		// A resize recreates the screen-sized transients and releases their old view handles for reuse, so a cached bind
		// group naming one would bind a destroyed view — or, once the handle is reissued, an unrelated one. Same reason
		// as ReleaseGraphResources; a resize is rare enough that rebuilding every group costs nothing worth keeping.
		_context.ClearBindGroups();
		_transients?.Resize(_width, _height);

	}

	// WebGPU has no host-side GPU idle wait, and it defers destruction of in-flight resources internally, so
	// the release-after-drain boundaries EngineBase sequences around (SetGraph, GrowStorage, Dispose) are safe
	// without an explicit wait.
	public override void WaitIdle() { }

	protected override Mesh UploadMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices) => _meshes.Create(vertices, indices);

	protected override Mesh UploadMesh<T>(ReadOnlySpan<T> vertices, VertexLayout layout, ReadOnlySpan<uint> indices)
	{
		var stride = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
		if (stride != layout.Stride)
			throw new WebGpuException($"Vertex element size ({stride} bytes) does not match the layout stride ({layout.Stride} bytes).");
		return _meshes.Create(System.Runtime.InteropServices.MemoryMarshal.AsBytes(vertices), stride, indices);
	}

	protected override Mesh UploadSkinnedMesh(ReadOnlySpan<SkinnedVertex> vertices, ReadOnlySpan<uint> indices) => _meshes.CreateSkinned(vertices, indices);

	// The buffers' handles go back to the allocator on release and are handed to the next buffers created, so the bind
	// groups naming them go first — the skinning dispatch caches one naming a mesh's bind-pose buffer.
	protected override void RetireMesh(Mesh mesh)
	{
		var (vertex, index) = _meshes.Buffers(mesh.Id);
		_context.EvictBindGroups(BoundObject.Buffer, vertex);
		_context.EvictBindGroups(BoundObject.Buffer, index);
		_meshes.Release(mesh.Id);
	}

	protected override Texture UploadTexture(TextureData data) => _textures.Create(data);

	// The view's handle goes back to the allocator on release and is handed to the next view created, so the bind groups
	// naming it go first — a draw list caches one per texture it samples.
	protected override void RetireTexture(Texture texture)
	{
		_context.EvictBindGroups(BoundObject.View, _textures.View(texture.Id));
		_textures.Release(texture.Id);
	}

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
		_layerTargets.Dispose();
		if (_captureBuffer >= 0)
			_handles.Release(WebGpuHandleAllocator.Buffers, _captureBuffer);
	}
}
