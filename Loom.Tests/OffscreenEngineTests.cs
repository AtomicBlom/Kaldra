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
