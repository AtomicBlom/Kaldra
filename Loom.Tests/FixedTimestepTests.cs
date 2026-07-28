using System.Numerics;

using Loom.Core.Animation;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Tests;

/// <summary>
/// <see cref="LoomApplication"/>'s fixed-timestep accumulator, driven through <see cref="IHostedApplication.Tick"/>
/// with a no-op engine (no GPU). Covers the catch-up loop, the render-interpolation <see cref="FrameTime.Alpha"/>,
/// the leftover-accumulator carry across frames, and the spiral-of-death clamp — none of which the offscreen
/// smoke test exercises.
/// </summary>
public class FixedTimestepTests
{
	private const int MaxCatchUpSteps = 5; // mirrors LoomApplication.MaxCatchUpSteps (the spiral-of-death guard).

	private static (FixedApp app, IHostedApplication driver) Start(double fixedStepHz)
	{
		var app = new FixedApp(fixedStepHz);
		var driver = (IHostedApplication)app;
		driver.ReadOptions();               // runs Configure → captures FixedStepHz
		driver.Load(new NoopEngine(), new NoopHostControl());
		return (app, driver);
	}

	[Test]
	public void Disabled_by_default_never_calls_fixed_update_and_leaves_alpha_zero()
	{
		var (app, driver) = Start(0); // FixedStepHz = 0 (the default)
		driver.Tick(new FrameTime(1.0 / 60.0, 1.0 / 60.0));

		app.FixedSteps.ShouldBeEmpty();
		app.LastAlpha.ShouldBe(0f);
	}

	[Test]
	public void One_step_per_frame_when_delta_matches_the_fixed_step()
	{
		var (app, driver) = Start(60);
		driver.Tick(new FrameTime(1.0 / 60.0, 1.0 / 60.0));

		app.FixedSteps.Count.ShouldBe(1);
		app.FixedSteps[0].DeltaSeconds.ShouldBe(1.0 / 60.0, 1e-9); // always the fixed step, not the frame delta
		app.LastAlpha.ShouldBe(0f, 1e-4f);
	}

	[Test]
	public void Several_steps_run_when_the_frame_delta_covers_several_fixed_steps()
	{
		var (app, driver) = Start(60);
		driver.Tick(new FrameTime(4.0 / 60.0, 4.0 / 60.0)); // four fixed steps' worth of time

		app.FixedSteps.Count.ShouldBe(4);
		app.LastAlpha.ShouldBe(0f, 1e-4f);
	}

	[Test]
	public void Leftover_time_becomes_alpha_and_carries_into_the_next_frame()
	{
		var (app, driver) = Start(100); // step = 0.01s

		driver.Tick(new FrameTime(0.018, 0.018)); // one step (0.01), 0.008 left over
		app.FixedSteps.Count.ShouldBe(1);
		app.LastAlpha.ShouldBe(0.8f, 1e-4f);      // 0.008 / 0.01

		driver.Tick(new FrameTime(0.007, 0.025)); // carried 0.008 + 0.007 = 0.015 → one more step, 0.005 left over
		app.FixedSteps.Count.ShouldBe(2);
		app.LastAlpha.ShouldBe(0.5f, 1e-4f);      // 0.005 / 0.01

		// TotalSeconds accumulates in fixed steps (each step's start time), independent of wall-clock frame time.
		app.FixedSteps[0].TotalSeconds.ShouldBe(0.00, 1e-9);
		app.FixedSteps[1].TotalSeconds.ShouldBe(0.01, 1e-9);
	}

	[Test]
	public void A_huge_frame_delta_is_clamped_and_the_backlog_dropped()
	{
		var (app, driver) = Start(60);
		driver.Tick(new FrameTime(1.0, 1.0)); // 60 steps' worth — must not run 60 times

		app.FixedSteps.Count.ShouldBe(MaxCatchUpSteps); // clamped
		app.LastAlpha.ShouldBe(0f);                     // backlog dropped → accumulator reset, no partial step
	}

	// An app that records every FixedUpdate it receives and the Alpha it last saw in Update.
	private sealed class FixedApp(double fixedStepHz) : LoomApplication
	{
		public List<FixedTime> FixedSteps { get; } = [];
		public float LastAlpha { get; private set; }

		protected override void Configure(AppOptions options) => options.FixedStepHz = fixedStepHz;
		protected override void FixedUpdate(FixedTime time) => FixedSteps.Add(time);
		protected override void Update(FrameTime time) => LastAlpha = time.Alpha;
	}

	private sealed class NoopHostControl : IHostControl
	{
		public void RequestExit() { }
	}

	// A do-nothing engine: Tick only needs Engine.Render to exist (a no-op here). Everything a fixed-timestep
	// tick never touches throws, so a stray dependency would surface loudly rather than silently pass.
	private sealed class NoopEngine : IEngine
	{
		public string BackendName => "noop";
		public string DeviceName => "noop";
		public Matrix4x4 ClipSpaceCorrection => Matrix4x4.Identity;
		public bool GpuTimingEnabled { get; set; }
		public bool ResourceInspectionEnabled { get; set; }
		public IReadOnlyList<PassTiming> LastFrameTimings => [];
		public double LastFrameGpuMilliseconds => 0;
		public IReadOnlyList<InspectableResource> InspectableResources => [];

		public void Render(FrameInputs inputs) { }
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
		public Material CreateMaterial(string technique, params ReadOnlySpan<Texture> textures) => throw new NotSupportedException();
		public Material CreateMaterial<T>(string technique, in T parameters, params ReadOnlySpan<Texture> textures) where T : unmanaged => throw new NotSupportedException();
		public void Write<T>(Material material, in T parameters) where T : unmanaged => throw new NotSupportedException();
		public MaterialLayout MaterialLayoutOf(string technique) => throw new NotSupportedException();
		public void Write<T>(ConstantsHandle handle, in T value) where T : unmanaged => throw new NotSupportedException();
		public void Write<T>(StorageBufferHandle handle, ReadOnlySpan<T> values) where T : unmanaged => throw new NotSupportedException();
		public ReadOnlySpan<byte> Read(ResourceHandle handle) => throw new NotSupportedException();
	}
}
