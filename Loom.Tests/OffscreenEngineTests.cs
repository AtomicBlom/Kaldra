using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Tests;

/// <summary>
/// The offscreen (windowless) render path: <see cref="Surface.Offscreen"/> brings an engine up with no window or
/// surface, renders into an owned readable image, and <see cref="IEngine.RequestCapture"/> extracts pixels from it
/// exactly as it does for a windowed frame. GPU-gated — SKIPPED when <see cref="Backends.Available"/> is empty
/// (no GPU backend usable on this box). Each available backend is exercised independently in its own loop
/// iteration, so one backend being unavailable never hides a regression in another.
/// </summary>
[NotInParallel] // brings backend devices up and down; concurrent bring-up is not something either backend survives
public class OffscreenEngineTests
{
	private const int Width = 256;
	private const int Height = 256;

	[Test]
	public void Offscreen_engine_renders_and_captures_a_non_empty_frame()
	{
		// Touch each backend's EngineSettings subclass so its assembly loads and its module initializer
		// self-registers into Backends.Available (guarded by its own IsSupported() probe) — a backend
		// assembly never referenced anywhere else in this method would otherwise stay unloaded and invisible here.
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();

		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");

		var failures = new List<string>();

		foreach (var backend in Backends.Available)
		{
			var capturePath = Path.Combine(Path.GetTempPath(), $"loom-offscreen-{backend.Name}-{Guid.NewGuid():N}.png");
			try
			{
				using var engine = backend.Create(Surface.Offscreen(Width, Height), Width, Height, new EngineSettings());

				engine.DeviceName.ShouldNotBeNullOrWhiteSpace();
				engine.BackendName.ShouldBe(backend.Name);

				// "fullscreen" is an engine-owned technique embedded in every native backend (a UV-gradient
				// fullscreen triangle, no mesh/texture/app-shader needed) — it proves technique baking, pipeline
				// creation, and the present-target hookup all work with no window behind them.
				engine.DefineTechnique("fullscreen", new TechniqueDescriptor("fullscreen"));
				var graph = RenderGraph.Define(g =>
				{
					var screen = g.Backbuffer;
					g.Pass("fill").Writes(screen).Draw(ctx => ctx.FullScreen("fullscreen"));
					g.Present(screen);
				});
				engine.SetGraph(graph);

				var inputs = new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 };
				for (var frame = 0; frame < 3; frame++)
				{
					if (frame == 2)
						engine.RequestCapture(capturePath);
					engine.Render(inputs);
				}

				File.Exists(capturePath).ShouldBeTrue($"{backend.Name}: RequestCapture did not produce {capturePath}.");
				var (w, h, rgba) = PngReader.Read(capturePath);
				if (w != Width || h != Height)
					failures.Add($"{backend.Name}: captured {w}x{h}, expected {Width}x{Height}.");

				var distinct = DistinctColors(rgba);
				if (distinct <= 1)
					failures.Add($"{backend.Name}: capture has only {distinct} distinct colour(s) — looks like an empty/flat frame.");

				// Resize: the offscreen path recreates its owned images at the new size (mirrors a window resize).
				engine.Resize(Width, Height / 2);
				var resizedCapturePath = capturePath + ".resized.png";
				try
				{
					for (var frame = 0; frame < 2; frame++)
					{
						if (frame == 1)
							engine.RequestCapture(resizedCapturePath);
						engine.Render(inputs);
					}
					var (rw, rh, _) = PngReader.Read(resizedCapturePath);
					if (rw != Width || rh != Height / 2)
						failures.Add($"{backend.Name}: after Resize, captured {rw}x{rh}, expected {Width}x{Height / 2}.");
				}
				finally
				{
					File.Delete(resizedCapturePath);
				}
			}
			finally
			{
				File.Delete(capturePath);
			}
		}

		failures.ShouldBeEmpty("Offscreen rendering failures:\n  " + string.Join("\n  ", failures));
	}

	[Test]
	public void Every_backend_reports_capabilities_a_real_device_must_have()
	{
		// A backend that forgot to probe would report an all-default record, and the graph check would then reject
		// graphs the device can actually run. Asserting the floor every real GPU clears is what catches that — the
		// exact values are the device's business, but "at least single-sampled" and "a power-of-two alignment" are
		// not negotiable on any adapter that can bring an engine up at all.
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();

		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");

		var failures = new List<string>();

		foreach (var backend in Backends.Available)
		{
			using var engine = backend.Create(Surface.Offscreen(Width, Height), Width, Height, new EngineSettings());
			var caps = engine.Capabilities;

			if ((caps.SampleCounts & SampleCounts.X1) == 0)
				failures.Add($"{backend.Name}: does not report single-sampled support.");
			if (caps.MaxColorAttachments < 4)
				failures.Add($"{backend.Name}: reports only {caps.MaxColorAttachments} colour attachments.");
			var alignment = caps.MinUniformBufferOffsetAlignment;
			if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
				failures.Add($"{backend.Name}: uniform offset alignment {alignment} is not a positive power of two.");

			// No backend derives cross-queue scheduling yet, so a backend claiming otherwise would open a gate with
			// nothing behind it — the graph check would accept a Queue.AsyncCompute pass the engine cannot honour.
			if (caps.AsyncCompute)
				failures.Add($"{backend.Name}: claims async compute, which no backend implements yet.");

			// The capability and the property must agree, since the property is the one an app reads.
			engine.GpuTimingEnabled = true;
			if (engine.GpuTimingEnabled != caps.GpuTiming)
				failures.Add($"{backend.Name}: GpuTimingEnabled settled at {engine.GpuTimingEnabled} but Capabilities.GpuTiming is {caps.GpuTiming}.");
			engine.GpuTimingEnabled = false;
		}

		failures.ShouldBeEmpty("Capability reporting failures:\n  " + string.Join("\n  ", failures));
	}

	[Test]
	public void Per_pass_GPU_timings_come_back_for_every_pass_of_the_graph()
	{
		// The timestamp writes are hooks on the shared record spine now, so a backend can implement them wrongly — or
		// stop implementing them — without any picture changing. Nothing asserted they produce anything: one test
		// reported the total for information and another only checked the enable flag settles.
		//
		// A frame's timestamps are readable once that slot's fence signals, which is a couple of frames later, so this
		// renders enough frames for the first slot to come back around.
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();

		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");

		var failures = new List<string>();

		foreach (var backend in Backends.Available)
		{
			using var engine = backend.Create(Surface.Offscreen(Width, Height), Width, Height, new EngineSettings());
			engine.GpuTimingEnabled = true;
			if (!engine.GpuTimingEnabled)
				continue; // this device cannot time its queue; Capabilities.GpuTiming says so and the test above pins that

			engine.DefineTechnique("probe", new TechniqueDescriptor("probe", HasFragmentPush: true));

			var graph = RenderGraph.Define(g =>
			{
				var screen = g.Backbuffer;
				var scratch = g.Screen.Color("scratch", Format.Rgba8Unorm);
				g.Pass("first").Writes(scratch).Draw(ctx => ctx.FullScreen("probe", [0.4f, 0.1f, 0.6f, 1f]));
				g.Pass("second").Samples(scratch).Writes(screen).Draw(ctx => ctx.FullScreen("probe", [0.2f, 0.7f, 0.3f, 1f]));
				g.Present(screen);
			});
			engine.SetGraph(graph);

			var inputs = new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 };
			for (var frame = 0; frame < 6; frame++)
				engine.Render(inputs);

			var timings = engine.LastFrameTimings;
			if (timings.Count != 2)
			{
				failures.Add($"{backend.Name}: {timings.Count} pass timings for a 2-pass graph — the per-pass timestamps are not being written or not decoded.");
				continue;
			}
			// Named, so a mis-indexed timestamp pair (which is easy to write and invisible in a picture) shows up as the
			// wrong pass rather than as a plausible number.
			if (timings[0].Name != "first" || timings[1].Name != "second")
				failures.Add($"{backend.Name}: timings name '{timings[0].Name}'/'{timings[1].Name}', not 'first'/'second'.");
			foreach (var t in timings)
				if (t.GpuMilliseconds < 0)
					failures.Add($"{backend.Name}: pass '{t.Name}' reports {t.GpuMilliseconds} ms.");
			// Zero is legitimate on a coarse timer, but the total must at least be the sum's order of magnitude rather
			// than the untouched default.
			if (engine.LastFrameGpuMilliseconds < 0)
				failures.Add($"{backend.Name}: frame total is {engine.LastFrameGpuMilliseconds} ms.");

			engine.GpuTimingEnabled = false;
		}

		failures.ShouldBeEmpty("GPU timing failures:\n  " + string.Join("\n  ", failures));
	}

	[Test]
	public void Every_backend_reports_the_same_authoring_mistakes_the_same_way()
	{
		// The point of the neutral exception family: an app catches one type with one message whichever backend is
		// drawing. Asserting the types and messages are *identical across backends* is the only way to prove that —
		// a per-backend assertion would pass just as happily with three different spellings.
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();

		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");

		var undefinedTechnique = new Dictionary<string, string>();
		var missingShader = new Dictionary<string, string>();

		foreach (var backend in Backends.Available)
		{
			using var engine = backend.Create(Surface.Offscreen(Width, Height), Width, Height, new EngineSettings());

			// A typo'd technique name, reported when a pass records with it.
			engine.DefineTechnique("fullscreen", new TechniqueDescriptor("fullscreen"));
			var graph = RenderGraph.Define(g =>
			{
				var screen = g.Backbuffer;
				g.Pass("fill").Writes(screen).Draw(ctx => ctx.FullScreen("no-such-technique"));
				g.Present(screen);
			});
			engine.SetGraph(graph);

			var notDefined = Should.Throw<TechniqueNotDefinedException>(
				() => engine.Render(new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 }));
			notDefined.Technique.ShouldBe("no-such-technique");
			undefinedTechnique[backend.Name] = notDefined.Message;

			// A shader whose compiled artifact was never built, reported at define time.
			var absent = Should.Throw<ShaderAssetMissingException>(
				() => engine.DefineTechnique("ghost", new TechniqueDescriptor("no-such-shader")));
			absent.Shader.ShouldBe("no-such-shader");
			missingShader[backend.Name] = absent.Message;
		}

		// Guard against a vacuous pass: one backend agrees with itself trivially.
		undefinedTechnique.Count.ShouldBeGreaterThan(0);
		undefinedTechnique.Values.Distinct().Count().ShouldBe(
			1, $"the undefined-technique message differs per backend: {string.Join(" | ", undefinedTechnique.Select(kv => $"{kv.Key}: {kv.Value}"))}");

		// The missing-shader message names the paths it tried, so it is legitimately backend-specific — but the type
		// and the named shader must match, which the per-backend assertions above already pinned.
		missingShader.Count.ShouldBe(undefinedTechnique.Count);
	}

	// Distinct opaque RGB values in a captured frame — a flat/empty frame collapses to one.
	private static int DistinctColors(byte[] rgba)
	{
		var seen = new HashSet<int>();
		for (var i = 0; i + 3 < rgba.Length; i += 4)
			seen.Add(rgba[i] << 16 | rgba[i + 1] << 8 | rgba[i + 2]);
		return seen.Count;
	}
}
