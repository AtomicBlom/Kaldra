#if LOOM_DX12
using System.Numerics;

using Loom.Core;
using Loom.DX12; // the one sanctioned backend type the host names; constructing DX12EngineSettings self-registers it
using Loom.Sandbox.Demos;

using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Loom.Sandbox;

// First-slice harness for the DirectX 12 backend (Planning.md §2 portability proof). The demo gallery in
// Program.cs is graph-driven and Vulkan-surface-specific; the DX12 backend's first slice — like Vulkan's own
// M0/M1 — predates the graph executor, so it gets a focused harness here: a window, the registry-selected DX12
// engine picked by name (the host names no graphics-API type beyond DX12EngineSettings), and a clear-to-color
// loop (the hand-baked triangle follows). Build with -p:UseDX12=true and select it at runtime with
// LOOM_BACKEND=DX12; LOOM_SMOKE_FRAMES=N runs N frames headless then exits, as the Vulkan path does.
internal static class Dx12Smoke
{
	public static void Run()
	{
		var vsyncEnv = Environment.GetEnvironmentVariable("LOOM_VSYNC");
		var vsync = vsyncEnv is null || vsyncEnv.ToLowerInvariant() is not ("0" or "false" or "off" or "no");
		var smokeFrames = int.TryParse(Environment.GetEnvironmentVariable("LOOM_SMOKE_FRAMES"), out var n) ? n : 0;
		var capturePath = Environment.GetEnvironmentVariable("LOOM_CAPTURE");

		// A plain Win32 window with no GL/Vulkan context — the DX12 backend binds its swapchain to the raw HWND.
		var options = WindowOptions.Default with
		{
			Size = new Vector2D<int>(1280, 720),
			Title = "Loom - DX12 backend (first slice)",
			API = GraphicsAPI.None,
		};
		using var window = Window.Create(options);
		window.Initialize();

		var hwnd = window.Native?.Win32?.Hwnd
			?? throw new InvalidOperationException("The DX12 smoke harness needs the window's Win32 HWND.");

		// Constructing DX12EngineSettings loads Loom.DX12, whose module initializer registers the backend.
		var settings = new DX12EngineSettings
		{
			EnableValidation = true,
			VSync = vsync,
			PreferredBackend = "DX12",
		};
		var backend = Engine.AvailableEngines.FirstOrDefault(e => e.Name == settings.PreferredBackend)
			?? throw new InvalidOperationException("The DX12 backend did not register into Engine.AvailableEngines.");
		using var engine = backend.Create(hwnd, window.FramebufferSize.X, window.FramebufferSize.Y, settings);

		// Two env-selected graph modes exercise the executor (vs. the fixed clear+triangle path):
		//   LOOM_DX12_GRAPH=1 — a two-pass offscreen->blit gradient (transient alloc + the cross-pass barrier).
		//   LOOM_DX12_CUBE=1  — the lit cube + ground plane on a depth buffer (mesh upload, indexed draw, the
		//                       MVP root constant, and a depth target), the DX12 twin of CubeLightingDemo.
		var graphMode = Environment.GetEnvironmentVariable("LOOM_DX12_GRAPH") is "1" or "true";
		var cubeMode = Environment.GetEnvironmentVariable("LOOM_DX12_CUBE") is "1" or "true";

		Mesh cube = default, plane = default;
		if (cubeMode)
		{
			engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
			var (cubeVerts, cubeIndices) = Geometry.BuildCube();
			var (planeVerts, planeIndices) = Geometry.BuildPlane();
			cube = engine.CreateMesh(cubeVerts, cubeIndices);
			plane = engine.CreateMesh(planeVerts, planeIndices);

			var graph = RenderGraph.Define(g =>
			{
				var screen = g.Backbuffer;
				var depth = g.Screen.Depth("depth", Format.Depth32F);
				g.Pass("scene")
					.Writes(screen)
					.WritesDepth(depth)
					.Draw(ctx =>
					{
						ctx.Draw(cube, "lit");
						ctx.Draw(plane, "lit");
					});
				g.Present(screen);
			});
			engine.SetGraph(graph);
		}
		else if (graphMode)
		{
			engine.DefineTechnique("gradient", new TechniqueDescriptor("gradient"));
			engine.DefineTechnique("blit", new TechniqueDescriptor("blit", LinearFilter: true));
			// Two passes: render a gradient into an offscreen target, then sample it into the backbuffer. This
			// exercises the executor's transient allocation + the COLOR_ATTACHMENT->SHADER_READ barrier between
			// the passes + the SRV/sampler descriptor-table path.
			var graph = RenderGraph.Define(g =>
			{
				var offscreen = g.Screen.Color("offscreen", Format.Rgba8Unorm);
				g.Pass("gradient").Writes(offscreen).Draw(ctx => ctx.FullScreen("gradient"));
				g.Pass("blit").Samples(offscreen).Writes(g.Backbuffer).Draw(ctx => ctx.FullScreen("blit", offscreen));
				g.Present(g.Backbuffer);
			});
			engine.SetGraph(graph);
		}

		var mode = cubeMode ? "lit cube" : graphMode ? "graph executor" : "first slice";
		Console.WriteLine($"Loom DX12 {mode} on {engine.DeviceName} ({engine.BackendName}). Close the window to exit.");

		var frame = 0;
		var elapsed = 0f;
		window.FramebufferResize += size => engine.Resize(size.X, size.Y);
		window.Render += delta =>
		{
			frame++;
			elapsed += (float)delta;

			// Orbit the cube. Neutral right-handed, +Y-up projection — the DX12 backend ingests it as-is (no
			// Y-flip, unlike Vulkan), since D3D12 clip space is already +Y-up (Camera/M7-B5).
			var eye = new Vector3(MathF.Sin(elapsed) * 2.5f, 1.6f, MathF.Cos(elapsed) * 2.5f);
			var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
			var aspect = window.FramebufferSize.Y == 0 ? 1f : (float)window.FramebufferSize.X / window.FramebufferSize.Y;
			var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, aspect, 0.1f, 100f);
			var camera = new Camera(view, projection);

			// LOOM_CAPTURE=path grabs the frame to a PNG (a few frames in, so the swapchain has settled),
			// then exits — the headless pixel-level proof the scene actually renders.
			if (capturePath is not null && frame == 3)
				engine.RequestCapture(capturePath);

			engine.Render(new FrameInputs { DeltaSeconds = delta, Time = elapsed, Camera = camera });

			if (capturePath is not null && frame >= 3)
				window.Close();
			else if (smokeFrames > 0 && frame >= smokeFrames)
				window.Close();
		};

		window.Run();
		engine.WaitIdle();
	}
}
#endif
