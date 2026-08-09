using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Tests;

/// <summary>
/// A hosted <see cref="LoomApplication"/> driven through its full lifecycle on an offscreen engine — the in-process
/// stand-in for launching a real desktop app (the shape <c>samples/HelloTriangle</c> takes) headless. It runs the
/// same steps the reusable desktop host drives (<c>ReadOptions → Load → Resize → Tick → Unload</c>, plus
/// <c>Exit()</c> routing and <see cref="LoomApplication.SurfaceSize"/>) and confirms the app renders a non-empty
/// frame — with no window and no host capture/smoke CLI mode. This is how a hosted app is smoke-tested now that
/// those test-only flags don't live on the production host. GPU-gated — SKIPPED when no backend is usable here.
/// </summary>
[NotInParallel] // brings a backend device up and down; concurrent bring-up is not something either backend survives
public class HostedApplicationSmokeTests
{
	private const int Width = 256;
	private const int Height = 256;
	private const int Frames = 3;

	[Test]
	public void Hosted_application_launches_and_renders_offscreen_through_its_lifecycle()
	{
		// Touch each backend's EngineSettings subclass so its assembly loads and its module initializer registers
		// into Backends.Available (guarded by its own IsSupported() probe).
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();

		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");

		var failures = new List<string>();

		foreach (var backend in Backends.Available)
		{
			var capturePath = Path.Combine(Path.GetTempPath(), $"loom-hosted-{backend.Name}-{Guid.NewGuid():N}.png");
			try
			{
				using var engine = backend.Create(Surface.Offscreen(Width, Height), Width, Height, new EngineSettings());

				var app = new SmokeApp();
				var driver = (IHostedApplication)app;
				var host = new RecordingHostControl();

				// Drive the exact lifecycle the desktop host drives, minus the window.
				_ = driver.ReadOptions();     // runs Configure
				driver.Load(engine, host);    // binds Engine + runs LoadContent (defines the technique, sets the graph)
				driver.Resize(Width, Height); // sets SurfaceSize + runs OnResize, as the host does before the first frame

				for (var frame = 0; frame < Frames; frame++)
				{
					if (frame == Frames - 1)
						engine.RequestCapture(capturePath);
					driver.Tick(new FrameTime(1.0 / 60.0, frame / 60.0)); // runs Update/Draw + the framework's Engine.Render
				}

				driver.Unload();              // runs UnloadContent
				engine.WaitIdle();

				// The lifecycle hooks all fired, in the expected shape.
				if (!app.Configured) failures.Add($"{backend.Name}: Configure did not run.");
				if (!app.Loaded) failures.Add($"{backend.Name}: LoadContent did not run.");
				if (app.UpdateCount < Frames) failures.Add($"{backend.Name}: Update ran {app.UpdateCount}x, expected {Frames}.");
				if (!app.Resized) failures.Add($"{backend.Name}: OnResize did not run.");
				if (!app.Unloaded) failures.Add($"{backend.Name}: UnloadContent did not run.");
				if (app.SurfaceSize.X != Width || app.SurfaceSize.Y != Height)
					failures.Add($"{backend.Name}: SurfaceSize is {app.SurfaceSize.X}x{app.SurfaceSize.Y}, expected {Width}x{Height}.");
				if (!host.ExitRequested) failures.Add($"{backend.Name}: Exit() did not route to IHostControl.RequestExit.");

				// The app actually drew something offscreen (a black/empty frame collapses to one distinct colour).
				if (!File.Exists(capturePath))
				{
					failures.Add($"{backend.Name}: RequestCapture produced no file.");
					continue;
				}
				var (w, h, rgba) = PngReader.Read(capturePath);
				if (w != Width || h != Height)
					failures.Add($"{backend.Name}: captured {w}x{h}, expected {Width}x{Height}.");
				if (DistinctColors(rgba) <= 1)
					failures.Add($"{backend.Name}: capture has <= 1 distinct colour — looks like an empty/flat frame.");
			}
			finally
			{
				File.Delete(capturePath);
			}
		}

		failures.ShouldBeEmpty("Hosted application smoke failures:\n  " + string.Join("\n  ", failures));
	}

	// Distinct opaque RGB values in a captured frame — a flat/empty frame collapses to one.
	private static int DistinctColors(byte[] rgba)
	{
		var seen = new HashSet<int>();
		for (var i = 0; i + 3 < rgba.Length; i += 4)
			seen.Add(rgba[i] << 16 | rgba[i + 1] << 8 | rgba[i + 2]);
		return seen.Count;
	}

	// A minimal application with the shape HelloTriangle takes: one fullscreen pass onto the backbuffer, no mesh,
	// camera, or app shader (the engine-embedded "fullscreen" UV-gradient technique). It records which lifecycle
	// hooks ran and quits itself on its final frame, so the test can assert the whole host contract end to end.
	private sealed class SmokeApp : LoomApplication
	{
		public bool Configured { get; private set; }
		public bool Loaded { get; private set; }
		public int UpdateCount { get; private set; }
		public bool Resized { get; private set; }
		public bool Unloaded { get; private set; }

		protected override void Configure(AppOptions options)
		{
			options.Title = "SmokeApp";
			Configured = true;
		}

		protected override void LoadContent()
		{
			Engine.DefineTechnique("fullscreen", new TechniqueDescriptor("fullscreen"));
			SetGraph(RenderGraph.Define(g =>
			{
				var screen = g.Backbuffer;
				g.Pass("fill").Writes(screen).Draw(static ctx => ctx.FullScreen("fullscreen"));
				g.Present(screen);
			}));
			Loaded = true;
		}

		protected override void Update(FrameTime time)
		{
			UpdateCount++;
			if (UpdateCount == Frames)
				Exit(); // a game quitting from its menu — must route through the host's IHostControl
		}

		protected override void OnResize(int width, int height) => Resized = true;

		protected override void UnloadContent() => Unloaded = true;
	}

	// Records the app's Exit() request without acting on it — a real host would close the window; the test only
	// needs to confirm the call routed here.
	private sealed class RecordingHostControl : IHostControl
	{
		public bool ExitRequested { get; private set; }

		public void RequestExit() => ExitRequested = true;
	}
}
