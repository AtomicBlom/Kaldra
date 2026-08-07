using System.Numerics;

using Loom.Core.Animation;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Tests;

/// <summary>
/// <see cref="RenderMode.OnDemand"/> gating, driven through <see cref="IHostedApplication.Tick"/> with a
/// render-counting no-op engine (no GPU): the first frame always renders, an idle app submits nothing, and
/// <see cref="LoomApplication.Invalidate"/> / a resize each schedule exactly one frame. Continuous mode still
/// renders every tick.
/// </summary>
public class OnDemandRenderTests
{
	private static readonly FrameTime AFrame = new(1.0 / 60.0, 1.0 / 60.0);

	private static (GatedApp app, IHostedApplication driver, CountingEngine engine, RecordingHostControl host) Start(RenderMode mode)
	{
		var app = new GatedApp(mode);
		var driver = (IHostedApplication)app;
		driver.ReadOptions();               // runs Configure → captures RenderMode
		var engine = new CountingEngine();
		var host = new RecordingHostControl();
		driver.Load(engine, host);
		return (app, driver, engine, host);
	}

	[Test]
	public void On_demand_renders_the_first_frame_then_idles()
	{
		var (_, driver, engine, _) = Start(RenderMode.OnDemand);

		driver.Tick(AFrame);
		engine.RenderCount.ShouldBe(1); // the first frame always renders, even with no invalidate

		driver.Tick(AFrame);
		driver.Tick(AFrame);
		engine.RenderCount.ShouldBe(1); // idle: nothing invalidated, no resize → no further submits
	}

	[Test]
	public void Invalidate_schedules_exactly_one_frame_and_wakes_the_host()
	{
		var (app, driver, engine, host) = Start(RenderMode.OnDemand);
		driver.Tick(AFrame); // first frame → 1

		app.RequestRedraw();
		host.RenderRequests.ShouldBe(1); // Invalidate wakes the host's event loop

		driver.Tick(AFrame);
		engine.RenderCount.ShouldBe(2); // the invalidation is drawn

		driver.Tick(AFrame);
		engine.RenderCount.ShouldBe(2); // and only once — the flag cleared after the submit
	}

	[Test]
	public void A_resize_forces_the_next_frame_even_without_invalidate()
	{
		var (_, driver, engine, _) = Start(RenderMode.OnDemand);
		driver.Tick(AFrame); // first frame → 1

		driver.Resize(800, 600);
		driver.Tick(AFrame);
		engine.RenderCount.ShouldBe(2); // a resize recreates the swapchain, so it must render

		driver.Tick(AFrame);
		engine.RenderCount.ShouldBe(2); // back to idle
	}

	[Test]
	public void Continuous_renders_every_tick()
	{
		var (_, driver, engine, host) = Start(RenderMode.Continuous);

		driver.Tick(AFrame);
		driver.Tick(AFrame);
		driver.Tick(AFrame);

		engine.RenderCount.ShouldBe(3);
		host.RenderRequests.ShouldBe(0); // Continuous never needs to wake the host
	}

	// A LoomApplication whose render mode is chosen per test; exposes the protected Invalidate for driving.
	private sealed class GatedApp(RenderMode mode) : LoomApplication
	{
		protected override void Configure(AppOptions options) => options.RenderMode = mode;

		public void RequestRedraw() => Invalidate();
	}

	private sealed class RecordingHostControl : IHostControl
	{
		public int RenderRequests { get; private set; }

		public void RequestExit() { }

		public void RequestRender() => RenderRequests++;
	}

	// A do-nothing engine that only counts Render submissions; everything a tick never touches throws, so a stray
	// dependency surfaces loudly. (Mirrors FixedTimestepTests.NoopEngine, plus the render counter.)
	private sealed class CountingEngine : IEngine
	{
		public int RenderCount { get; private set; }

		public string BackendName => "counting";
		public string DeviceName => "counting";
		public Matrix4x4 ClipSpaceCorrection => Matrix4x4.Identity;
		public bool GpuTimingEnabled { get; set; }
		public bool ResourceInspectionEnabled { get; set; }
		public IReadOnlyList<PassTiming> LastFrameTimings => [];
		public double LastFrameGpuMilliseconds => 0;
		public IReadOnlyList<InspectableResource> InspectableResources => [];

		public void Render(FrameInputs inputs) => RenderCount++;
		public void Resize(int width, int height) { }
		public void SetGraph(RenderGraph graph) { }
		public void ServicePendingUploads() { }
		public void RequestCapture(string path) { }
		public void WaitIdle() { }
		public void Dispose() { }

		public void DefineTechnique(string name, TechniqueDescriptor descriptor) => throw new NotSupportedException();
		public Mesh CreateMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices) => throw new NotSupportedException();
		public Task<Mesh> CreateMeshAsync(Vertex[] vertices, uint[] indices) => throw new NotSupportedException();
		public Mesh CreateMesh<T>(ReadOnlySpan<T> vertices, VertexLayout layout, ReadOnlySpan<uint> indices) where T : unmanaged => throw new NotSupportedException();
		public Mesh CreateSkinnedMesh(ReadOnlySpan<SkinnedVertex> vertices, ReadOnlySpan<uint> indices) => throw new NotSupportedException();
		public Task<Mesh> CreateSkinnedMeshAsync(SkinnedVertex[] vertices, uint[] indices) => throw new NotSupportedException();
		public Texture CreateTexture(TextureData data) => throw new NotSupportedException();
		public Task<Texture> CreateTextureAsync(TextureData data) => throw new NotSupportedException();
		public Material CreateMaterial(params ReadOnlySpan<Texture> textures) => throw new NotSupportedException();
		public Task<Material> CreateMaterialAsync(params Texture[] textures) => throw new NotSupportedException();
		public Task<Material> CreateMaterialAsync(string technique, params Texture[] textures) => throw new NotSupportedException();
		public Task<Material> CreateMaterialAsync<T>(string technique, T parameters, params Texture[] textures) where T : unmanaged => throw new NotSupportedException();
		public Material CreateMaterial(string technique, params ReadOnlySpan<Texture> textures) => throw new NotSupportedException();
		public Material CreateMaterial<T>(string technique, in T parameters, params ReadOnlySpan<Texture> textures) where T : unmanaged => throw new NotSupportedException();
		public void Write<T>(Material material, in T parameters) where T : unmanaged => throw new NotSupportedException();
		public MaterialLayout MaterialLayoutOf(string technique) => throw new NotSupportedException();
		public void Write<T>(ConstantsHandle handle, in T value) where T : unmanaged => throw new NotSupportedException();
		public void Write<T>(StorageBufferHandle handle, ReadOnlySpan<T> values) where T : unmanaged => throw new NotSupportedException();
		public ReadOnlySpan<byte> Read(ResourceHandle handle) => throw new NotSupportedException();
	}
}
