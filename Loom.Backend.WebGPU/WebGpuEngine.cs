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
	private readonly WebGpuRenderContext _context;
	private readonly Dictionary<string, TechniqueDescriptor> _techniques = [];

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
		_context = new WebGpuRenderContext(this);
		WebGpuInterop.ConfigureCanvas(_width, _height);
	}

	public override string BackendName => "WebGPU";
	public override string DeviceName => _surface.AdapterName;

	// Timestamp queries are an optional WebGPU feature and unavailable on many browsers/adapters; timing lands
	// once the feature is negotiated at device request.
	protected override bool CanEnableGpuTiming => false;

	// Phase-1 stub: record the descriptor so DefineTechnique succeeds. Real pipeline baking (WGSL module +
	// explicit bind-group layouts from the emitted WGSL) replaces this body when the technique path lands.
	protected override void BakeTechnique(string name, TechniqueDescriptor descriptor) => _techniques[name] = descriptor;

	protected override void ResetPipelineCaches() => _techniques.Clear();

	protected override void ReleaseGraphResources()
	{
		_transients?.Dispose();
		_transients = null;
	}

	protected override void RebuildGraphResources(RenderGraph authored, RenderGraph executed) =>
		_transients = new WebGpuTransientResources(_handles, executed, Barriers, _width, _height);

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

	public override Mesh CreateMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices) =>
		throw new WebGpuException("CreateMesh is not yet implemented on the WebGPU backend.");

	public override Mesh CreateMesh<T>(ReadOnlySpan<T> vertices, VertexLayout layout, ReadOnlySpan<uint> indices) =>
		throw new WebGpuException("CreateMesh is not yet implemented on the WebGPU backend.");

	public override Mesh CreateSkinnedMesh(ReadOnlySpan<SkinnedVertex> vertices, ReadOnlySpan<uint> indices) =>
		throw new WebGpuException("CreateSkinnedMesh is not yet implemented on the WebGPU backend.");

	public override Texture CreateTexture(TextureData data) =>
		throw new WebGpuException("CreateTexture is not yet implemented on the WebGPU backend.");

	public override Material CreateMaterial(params ReadOnlySpan<Texture> textures) =>
		throw new WebGpuException("CreateMaterial is not yet implemented on the WebGPU backend.");

	public override void Dispose()
	{
		_transients?.Dispose();
		_transients = null;
	}
}
