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
			// The Loom SDK copies the demo shaders' HLSL + reflection here next to the exe; the DX12 backend
			// resolves a technique's <name>.hlsl/.json from this directory (falling back to its built-ins).
			ShaderDirectory = Path.Combine(AppContext.BaseDirectory, "shaders"),
		};
		var backend = Engine.AvailableEngines.FirstOrDefault(e => e.Name == settings.PreferredBackend)
			?? throw new InvalidOperationException("The DX12 backend did not register into Engine.AvailableEngines.");
		using var engine = backend.Create(hwnd, window.FramebufferSize.X, window.FramebufferSize.Y, settings);

		// Env-selected graph modes exercise the executor (vs. the fixed clear+triangle path):
		//   LOOM_DX12_GRAPH=1     — a two-pass offscreen->blit gradient (transient alloc + the cross-pass barrier).
		//   LOOM_DX12_CUBE=1      — the lit cube + ground plane on a depth buffer (mesh upload, indexed draw, the
		//                           MVP root constant, and a depth target), the DX12 twin of CubeLightingDemo.
		//   LOOM_DX12_PARTICLES=1 — the GPU-particle path (§7.6): a compute pass writes a storage buffer, a draw
		//                           reads it as a per-instance vertex stream (additive sparks over the lit scene).
		//   LOOM_DX12_ALIAS=1     — a 4-transient blit chain whose non-overlapping lifetimes fold into 2 memory
		//                           buckets (the §8/M4 AliasPlan → DX12 placed-resource heaps + aliasing barriers).
		var graphMode = Environment.GetEnvironmentVariable("LOOM_DX12_GRAPH") is "1" or "true";
		var cubeMode = Environment.GetEnvironmentVariable("LOOM_DX12_CUBE") is "1" or "true";
		var particlesMode = Environment.GetEnvironmentVariable("LOOM_DX12_PARTICLES") is "1" or "true";
		var aliasMode = Environment.GetEnvironmentVariable("LOOM_DX12_ALIAS") is "1" or "true";

		Mesh cube = default, plane = default;
		if (cubeMode || particlesMode)
		{
			engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
			var (cubeVerts, cubeIndices) = Geometry.BuildCube();
			var (planeVerts, planeIndices) = Geometry.BuildPlane();
			cube = engine.CreateMesh(cubeVerts, cubeIndices);
			plane = engine.CreateMesh(planeVerts, planeIndices);

			RenderGraph graph;
			if (particlesMode)
			{
				engine.DefineTechnique("particle.update", new TechniqueDescriptor("particle_update", FragmentEntry: "computeMain", IsCompute: true, HasFragmentPush: true));
				engine.DefineTechnique("particle.render", new TechniqueDescriptor("particle_render", Blend: BlendMode.Additive, ParticleInput: true));
				engine.DefineTechnique("blit", new TechniqueDescriptor("blit", LinearFilter: true));

				const int particleCount = 4096;
				graph = RenderGraph.Define(g =>
				{
					var screen = g.Backbuffer;
					var lit = g.Screen.Color("lit", Format.Rgba16F);
					var depth = g.Screen.Depth("depth", Format.Depth32F);
					var particles = g.Buffer("particles", particleCount * 16); // float4 per particle

					g.Pass("scene")
						.Writes(lit)
						.WritesDepth(depth)
						.Draw(ctx =>
						{
							ctx.Draw(cube, "lit");
							ctx.Draw(plane, "lit");
						});

					// Compute writes the buffer; the draw reads it as a vertex stream — the engine derives the
					// compute->vertex-input barrier between the two edges (.ReadWrites then .ReadsVertex).
					g.Compute("particle.update")
						.ReadWrites(particles)
						.Dispatch(ctx => ctx.Dispatch("particle.update", ((uint)(particleCount / 64), 1, 1), [ctx.Time, particleCount], [], [particles]));

					g.Pass("particles")
						.Blends(lit)
						.ReadsVertex(particles)
						.Draw(ctx => ctx.DrawParticles(particles, particleCount, "particle.render"));

					g.Pass("blit").Samples(lit).Writes(screen).Draw(ctx => ctx.FullScreen("blit", lit));
					g.Present(screen);
				});
			}
			else
			{
				graph = RenderGraph.Define(g =>
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
			}
			engine.SetGraph(graph);
		}
		else if (aliasMode)
		{
			engine.DefineTechnique("gradient", new TechniqueDescriptor("gradient"));
			engine.DefineTechnique("blit", new TechniqueDescriptor("blit", LinearFilter: true));
			// A 4-link blit chain: each transient is written by one pass and consumed by the next, so their
			// lifetimes never overlap by more than one step. The AliasPlan folds a+c and b+d (4 transients → 2
			// memory buckets); the engine places each bucket's pair on one heap and emits the aliasing barrier on
			// each member's reactivation. A correct result is still the gradient (corruption would mean a missing
			// or mis-ordered aliasing barrier). The fold is logged at SetGraph.
			var graph = RenderGraph.Define(g =>
			{
				var a = g.Screen.Color("a", Format.Rgba8Unorm);
				var b = g.Screen.Color("b", Format.Rgba8Unorm);
				var c = g.Screen.Color("c", Format.Rgba8Unorm);
				var d = g.Screen.Color("d", Format.Rgba8Unorm);
				g.Pass("gen").Writes(a).Draw(ctx => ctx.FullScreen("gradient"));
				g.Pass("b1").Samples(a).Writes(b).Draw(ctx => ctx.FullScreen("blit", a));
				g.Pass("b2").Samples(b).Writes(c).Draw(ctx => ctx.FullScreen("blit", b));
				g.Pass("b3").Samples(c).Writes(d).Draw(ctx => ctx.FullScreen("blit", c));
				g.Pass("b4").Samples(d).Writes(g.Backbuffer).Draw(ctx => ctx.FullScreen("blit", d));
				g.Present(g.Backbuffer);
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

		var mode = particlesMode ? "GPU particles" : cubeMode ? "lit cube" : aliasMode ? "transient aliasing" : graphMode ? "graph executor" : "first slice";
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
