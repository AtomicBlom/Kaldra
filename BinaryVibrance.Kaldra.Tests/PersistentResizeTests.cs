using BinaryVibrance.Kaldra.Backend.DirectX12;
using BinaryVibrance.Kaldra.Backend.Vulkan;
using BinaryVibrance.Kaldra.Core.Diagnostics;
using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// What a window resize does to a <see cref="ResourceKind.Persistent"/> resource — one semantic, asserted on every
/// available backend, because this is precisely where the two backends used to disagree (one never resized a
/// persistent, the other rebuilt them all and lost their history). A persistent sized to a fixed domain is left
/// completely alone, so the cross-frame value it carries survives; one sized to the swapchain is reallocated at the
/// new extent and its contents are gone. GPU-gated — SKIPPED when no backend is available.
/// </summary>
[NotInParallel] // one GPU engine at a time, and KaldraLog.Sink is process-wide
public class PersistentResizeTests
{
	private const int W = 320, H = 240;
	private const int PerChannelTolerance = 4; // the cross-backend parity bar, reused for before/after a resize

	[Test]
	public void A_fixed_size_persistents_history_survives_a_resize()
	{
		foreach (var backend in AvailableBackends())
		{
			var before = TempPath(backend.Name, "before");
			var after = TempPath(backend.Name, "after");
			try
			{
				using var engine = Engine(backend);
				engine.SetGraph(ExposureLoop(g => g.Domain("meter", 1, 1).Persistent("exposure", Format.R32F)));

				var inputs = new FrameInputs { Time = 1.0, DeltaSeconds = 1.0 / 60.0 };
				// Let the temporal exposure converge, then capture the converged frame.
				for (var frame = 1; frame <= 62; frame++)
				{
					if (frame == 60)
						engine.RequestCapture(before);
					engine.Render(inputs);
				}

				// Resize to the same extent: nothing about the image changes, so the only thing under test is whether
				// rebuilding the graph's resources took the 1×1 exposure meter down with them.
				engine.Resize(W, H);
				for (var frame = 1; frame <= 3; frame++)
				{
					if (frame == 2)
						engine.RequestCapture(after);
					engine.Render(inputs);
				}

				var (bw, bh, a) = PngReader.Read(before);
				var (_, _, b) = PngReader.Read(after);
				// Guard against a vacuous pass: two flat frames would compare equal too.
				DistinctColors(a).ShouldBeGreaterThan(8, $"{backend.Name}: the pre-resize frame looks flat — the comparison below would pass vacuously.");

				var diff = ImageComparison.Compare(bw, bh, a, b, PerChannelTolerance);
				diff.FailedFraction.ShouldBeLessThan(
					0.005,
					$"{backend.Name}: the frame changed across a resize ({diff}) — the exposure history was wiped, so the adaptation restarted from an undefined value.");
			}
			finally
			{
				Delete(before, after);
			}
		}
	}

	[Test]
	public void Reallocating_a_screen_sized_persistent_on_resize_reports_nothing_wrong()
	{
		// A screen-sized persistent's *contents* can't survive a resize (there is no resample), so there is no image to
		// compare — what this pins is that reallocating it leaves the engine's own bookkeeping consistent. The trap is
		// the cross-frame layout each backend tracks for a persistent: a reallocated one is a *different* image that
		// starts undefined, so a barrier still claiming the layout the old image rested in is a lie the validation layer
		// catches immediately ("expects VkImage … to be in SHADER_READ_ONLY_OPTIMAL — current layout is UNDEFINED").
		// Both backends surface driver debug-layer output through KaldraLog, so capturing it is the assertion. Grow and
		// shrink are both exercised. (That the extent itself follows the swapchain is a property of the resource's
		// domain, pinned by DomainAndResourceTests — no public surface exposes an image's extent to assert here.)
		foreach (var backend in AvailableBackends())
		{
			var errors = new List<string>();
			var previousSink = KaldraLog.Sink;
			KaldraLog.Sink = (level, message) =>
			{
				if (level == KaldraLogLevel.Error)
					lock (errors) errors.Add(message);
			};
			try
			{
				var inputs = new FrameInputs { Time = 1.0, DeltaSeconds = 1.0 / 60.0 };
				// Scoped so the engine is disposed inside the capture: DirectX12 reports its debug-layer tally on teardown.
				using (var engine = Engine(backend))
				{
					engine.SetGraph(ExposureLoop(g => g.Persistent("history", Format.R32F)));
					for (var frame = 1; frame <= 4; frame++)
						engine.Render(inputs);

					engine.Resize(W / 2, H / 2);
					for (var frame = 1; frame <= 4; frame++)
						engine.Render(inputs);

					engine.Resize(W, H); // and back up again — growing is the direction that reads out of bounds
					for (var frame = 1; frame <= 4; frame++)
						engine.Render(inputs);
				}

				errors.ShouldBeEmpty($"{backend.Name} reported errors while resizing a graph with a screen-sized persistent:\n  " + string.Join("\n  ", errors));
			}
			finally
			{
				KaldraLog.Sink = previousSink;
			}
		}
	}

	// The tonemapping demo's auto-exposure loop, reduced to the parts that exercise a persistent: probe a flat HDR
	// value into a linear target, meter it into `persistent` with the real temporal compute, tone-map by it, blit out.
	// `persistent` is supplied by the caller so the same graph can be built around a fixed-size or a screen-sized one.
	private static RenderGraph ExposureLoop(Func<GraphBuilder, ColorHandle> persistent) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var lit = g.Screen.Color("lit", Format.Rgba16F);
		var graded = g.Screen.Color("graded", Format.Rgba16F);
		var exposure = persistent(g);

		g.Pass("probe").Writes(lit).Draw(ctx => ctx.FullScreen("probe.opaque", Hdr));
		g.Compute("exposure").Samples(lit).ReadWrites(exposure)
			.Dispatch(ctx => ctx.Dispatch("exposure", (1, 1, 1), [8f * (float)ctx.DeltaTime], [lit], [exposure]));
		g.Pass("tonemap").Samples(lit, exposure).Writes(graded)
			.Draw(ctx => ctx.FullScreen("tonemap", [0f], lit, exposure));
		g.Pass("blit").Samples(graded).Writes(screen).Draw(ctx => ctx.FullScreen("blit", graded));
		g.Present(screen);
	});

	private static readonly float[] Hdr = [8f, 8f, 8f, 1f]; // 0→8 across the screen: well into HDR, so exposure matters

	private static IEngine Engine(IRenderBackend backend)
	{
		var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
		engine.DefineTechnique("probe.opaque", new TechniqueDescriptor("probe", HasFragmentPush: true));
		engine.DefineTechnique("exposure", TechniqueDescriptor.Compute("exposure", hasFragmentPush: true));
		engine.DefineTechnique("tonemap", new TechniqueDescriptor("tonemap", HasFragmentPush: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
		return engine;
	}

	private static IReadOnlyList<IRenderBackend> AvailableBackends()
	{
		// Touch each backend's settings type so its assembly loads and self-registers into Backends.Available.
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");
		return Backends.Available;
	}

	private static string TempPath(string backend, string tag) =>
		Path.Combine(Path.GetTempPath(), $"kaldra-persist-{backend}-{tag}-{Guid.NewGuid():N}.png");

	private static void Delete(params string[] paths)
	{
		foreach (var path in paths)
			if (File.Exists(path))
				File.Delete(path);
	}

	private static int DistinctColors(byte[] rgba)
	{
		var seen = new HashSet<int>();
		for (var i = 0; i + 3 < rgba.Length; i += 4)
			seen.Add(rgba[i] << 16 | rgba[i + 1] << 8 | rgba[i + 2]);
		return seen.Count;
	}
}
