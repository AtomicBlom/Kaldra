using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Tests;

/// <summary>
/// <see cref="IEngine.SetGraph"/>'s bake cache: a graph structurally equal to the active one reuses its GPU
/// resources instead of draining and rebuilding them. The risk that buys is staleness — the cached plan carries the
/// <em>previous</em> graph's <c>.Draw</c>/<c>.Dispatch</c> closures, and reusing those would silently render whatever
/// they captured. This pins the guarantee that the cache hit is invisible: the frame after re-setting an equal graph
/// reflects the graph just set, not the one it replaced. GPU-gated — SKIPPED when no backend is available.
/// </summary>
[NotInParallel] // one GPU engine at a time
public class GraphBakeCacheTests
{
	private const int Width = 64;
	private const int Height = 64;

	// Flat opaque colours the probe technique pushes as its fragment constants — far enough apart that a stale frame
	// can't be mistaken for the fresh one.
	private static readonly float[] Red = [1f, 0f, 0f, 1f];
	private static readonly float[] Blue = [0f, 0f, 1f, 1f];

	[Test]
	public void Re_setting_an_equal_graph_renders_the_new_graphs_callbacks()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");

		var failures = new List<string>();
		foreach (var backend in Backends.Available)
		{
			var path = Path.Combine(Path.GetTempPath(), $"loom-bake-{backend.Name}-{Guid.NewGuid():N}.png");
			try
			{
				using var engine = backend.Create(Surface.Offscreen(Width, Height), Width, Height, new EngineSettings());
				engine.DefineTechnique("probe.opaque", new TechniqueDescriptor("probe", HasFragmentPush: true));

				// Two graphs that differ only in what their closure pushes — identical structure, so identical hash.
				var first = Fill(Red);
				var second = Fill(Blue);
				second.Hash().ShouldBe(first.Hash(), $"{backend.Name}: the two graphs must be structurally equal for this to test the cache hit at all.");

				engine.SetGraph(first);
				var inputs = new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 };
				engine.Render(inputs);

				engine.SetGraph(second); // bake-cache hit: no GPU rebuild, but the new closure must be the one that runs
				for (var frame = 1; frame <= 3; frame++)
				{
					if (frame == 2)
						engine.RequestCapture(path);
					engine.Render(inputs);
				}

				var (_, _, rgba) = PngReader.Read(path);
				// Sample the centre pixel: the probe fills the whole target with the pushed colour.
				var centre = ((Height / 2) * Width + Width / 2) * 4;
				if (rgba[centre + 2] <= rgba[centre])
					failures.Add($"{backend.Name}: after re-setting an equal graph the frame is still the first graph's (rgb {rgba[centre]},{rgba[centre + 1]},{rgba[centre + 2]}) — the cached plan stranded the old closures.");
			}
			finally
			{
				if (File.Exists(path)) File.Delete(path);
			}
		}

		failures.ShouldBeEmpty("SetGraph bake-cache failures:\n  " + string.Join("\n  ", failures));
	}

	private static RenderGraph Fill(float[] color) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		g.Pass("fill").Writes(screen).Draw(ctx => ctx.FullScreen("probe.opaque", color));
		g.Present(screen);
	});
}
