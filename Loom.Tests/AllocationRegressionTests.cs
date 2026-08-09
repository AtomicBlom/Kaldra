using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Tests;

/// <summary>
/// The "no allocations on the hot path" pillar, asserted mechanically. Once a graph is set and the first frames have
/// warmed the caches, <see cref="IEngine.Render"/> must allocate no managed memory at all — a steady loop that
/// allocates a little per frame is invisible in a demo and fatal in a game, because it turns into GC pauses under
/// load rather than a number anyone watches. A reviewer can't catch this by reading a diff; a counter can.
/// GPU-gated — SKIPPED when no backend is available.
/// </summary>
[NotInParallel] // one GPU engine at a time, and the allocation counter is per-thread
public class AllocationRegressionTests
{
	private const int W = 256, H = 192;
	private const int WarmupFrames = 20;  // pipeline bakes, descriptor-pool growth, the first readback/timing buffers
	private const int MeasuredFrames = 30;

	[Test]
	public void A_warm_render_loop_allocates_nothing_per_frame()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");

		var failures = new List<string>();
		foreach (var backend in Backends.Available)
		{
			using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
			engine.DefineTechnique("probe.opaque", new TechniqueDescriptor("probe", HasFragmentPush: true));
			engine.DefineTechnique("exposure", TechniqueDescriptor.Compute("exposure", hasFragmentPush: true));
			engine.DefineTechnique("tonemap", new TechniqueDescriptor("tonemap", HasFragmentPush: true));
			engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
			engine.SetGraph(BusyGraph());

			var inputs = new FrameInputs { Time = 1.0, DeltaSeconds = 1.0 / 60.0 };
			for (var frame = 0; frame < WarmupFrames; frame++)
				engine.Render(inputs);

			var before = GC.GetAllocatedBytesForCurrentThread();
			for (var frame = 0; frame < MeasuredFrames; frame++)
				engine.Render(inputs);
			var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

			if (allocated != 0)
				failures.Add($"{backend.Name}: {allocated} bytes over {MeasuredFrames} warm frames ({allocated / (double)MeasuredFrames:0.#} per frame).");
		}

		failures.ShouldBeEmpty("The steady per-frame render loop allocated managed memory:\n  " + string.Join("\n  ", failures));
	}

	// Busy enough to walk every per-frame code path that has bitten before: a compute dispatch, a cross-frame
	// persistent, several raster passes with derived barriers and load/store actions, pushed constants, and sampled
	// inputs bound per pass.
	//
	// Every argument each body passes is built once, with the graph, and only its *contents* change per frame — the
	// "capture stable objects, read their live members" half of the per-frame-closure pillar. That is deliberate: a
	// body that spelled its arguments inline (`[8f * ctx.DeltaTime]`, `[lit]`) would build those temporaries per
	// frame, and an unoptimized build puts them on the heap rather than the stack — so the measurement would be of
	// this file's call sites instead of the engine's frame path, and would read differently per configuration.
	private static RenderGraph BusyGraph() => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var lit = g.Screen.Color("lit", Format.Rgba16F);
		var graded = g.Screen.Color("graded", Format.Rgba16F);
		var exposure = g.Domain("meter", 1, 1).Persistent("exposure", Format.R32F);

		var adaptation = new float[1];      // written per frame, allocated once
		float[] sdr = [0f];                 // the tone-map's SDR selector
		ResourceHandle[] litOnly = [lit];
		ResourceHandle[] exposureOnly = [exposure];
		ResourceHandle[] litAndExposure = [lit, exposure];
		ResourceHandle[] gradedOnly = [graded];

		g.Pass("probe").Writes(lit).Draw(ctx => ctx.FullScreen("probe.opaque", Hdr, NoInputs));
		g.Compute("exposure").Samples(lit).ReadWrites(exposure)
			.Dispatch(ctx =>
			{
				adaptation[0] = 8f * (float)ctx.DeltaTime;
				ctx.Dispatch("exposure", (1, 1, 1), adaptation, litOnly, exposureOnly);
			});
		g.Pass("tonemap").Samples(lit, exposure).Writes(graded)
			.Draw(ctx => ctx.FullScreen("tonemap", sdr, litAndExposure));
		g.Pass("blit").Samples(graded).Writes(screen).Draw(ctx => ctx.FullScreen("blit", NoPush, gradedOnly));
		g.Present(screen);
	});

	private static readonly float[] Hdr = [4f, 4f, 4f, 1f];
	private static readonly float[] NoPush = [];
	private static readonly ResourceHandle[] NoInputs = [];
}
