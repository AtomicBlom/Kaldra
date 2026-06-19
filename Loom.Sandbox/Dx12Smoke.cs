#if LOOM_DX12
using System.Numerics;

#if LOOM_IMGUI
using ImGuiNET;
#endif
using Loom.Core;
using Loom.DX12; // the one sanctioned backend type the host names; constructing DX12EngineSettings self-registers it
using Loom.Sandbox.Demos;

using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Loom.Sandbox;

// Harness for the DirectX 12 backend (Planning.md §2 portability proof). The default path runs the SAME IDemo
// gallery Program.cs runs on Vulkan — identical graph descriptions and technique definitions, driven through
// the DX12 executor — which is the portability thesis made concrete. Two env-selected low-level proofs sit
// beside it for the executor's own machinery (transient alloc + cross-pass barrier; transient-memory aliasing).
//
// Build with -p:UseDX12=true and select it at runtime with LOOM_BACKEND=DX12. LOOM_SMOKE_FRAMES=N runs N frames
// headless then exits, and LOOM_CAPTURE=path grabs a frame to a PNG — both as the Vulkan path does.
internal static class Dx12Smoke
{
	public static void Run()
	{
		var vsyncEnv = Environment.GetEnvironmentVariable("LOOM_VSYNC");
		var vsync = vsyncEnv is null || vsyncEnv.ToLowerInvariant() is not ("0" or "false" or "off" or "no");
		var hdrEnv = Environment.GetEnvironmentVariable("LOOM_HDR");
		var hdr = hdrEnv is not null && hdrEnv.ToLowerInvariant() is "1" or "true" or "on" or "yes";
		var smokeFrames = int.TryParse(Environment.GetEnvironmentVariable("LOOM_SMOKE_FRAMES"), out var n) ? n : 0;
		var capturePath = Environment.GetEnvironmentVariable("LOOM_CAPTURE");

		// A plain Win32 window with no GL/Vulkan context — the DX12 backend binds its swapchain to the raw HWND.
		var options = WindowOptions.Default with
		{
			Size = new Vector2D<int>(1280, 720),
			Title = "Loom - DX12 backend (demo gallery)",
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
			HdrOutput = hdr,
			PreferredBackend = "DX12",
			// The Loom SDK copies the app shaders' signed DXIL + reflection JSON next to the exe under shaders\;
			// the DX12 backend resolves a technique's <name>.{vs,ps,cs}.dxil from this directory (engine shaders
			// resolve from Loom.DX12's embedded DXIL first), falling back to its built-in stand-ins.
			ShaderDirectory = Path.Combine(AppContext.BaseDirectory, "shaders"),
		};
		var backend = Engine.AvailableEngines.FirstOrDefault(e => e.Name == settings.PreferredBackend)
			?? throw new InvalidOperationException("The DX12 backend did not register into Engine.AvailableEngines.");
		using var engine = backend.Create(hwnd, window.FramebufferSize.X, window.FramebufferSize.Y, settings);

		// M13 GPU profiler: enable timing in a headless smoke run so the final frame can dump per-pass numbers for a
		// cross-backend sanity check against the Vulkan harness (no-op if the queue can't timestamp).
		if (smokeFrames > 0)
			engine.GpuTimingEnabled = true;

		//   LOOM_DX12_GRAPH=1 — a two-pass offscreen->blit gradient (transient alloc + the cross-pass barrier).
		//   LOOM_DX12_ALIAS=1 — a 4-transient blit chain whose non-overlapping lifetimes fold into 2 memory
		//                       buckets (the §8/M4 AliasPlan → DX12 placed-resource heaps + aliasing barriers).
		// With neither set, the real demo gallery runs (the default).
		var graphMode = Environment.GetEnvironmentVariable("LOOM_DX12_GRAPH") is "1" or "true";
		var aliasMode = Environment.GetEnvironmentVariable("LOOM_DX12_ALIAS") is "1" or "true";

		string mode;
		IDemo? activeDemo = null; // the gallery's selected demo, so the loop can run its per-frame Update (M12 cull)
		if (aliasMode)
		{
			engine.DefineTechnique("gradient", new TechniqueDescriptor("gradient"));
			engine.DefineTechnique("blit", new TechniqueDescriptor("blit", LinearFilter: true));
			// A 4-link blit chain: each transient is written by one pass and consumed by the next, so their
			// lifetimes never overlap by more than one step. The AliasPlan folds a+c and b+d (4 transients → 2
			// memory buckets); the engine places each bucket's pair on one heap and emits the aliasing barrier on
			// each member's reactivation. A correct result is still the gradient (corruption would mean a missing
			// or mis-ordered aliasing barrier). The fold is logged at SetGraph.
			engine.SetGraph(RenderGraph.Define(g =>
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
			}));
			mode = "transient aliasing";
		}
		else if (graphMode)
		{
			engine.DefineTechnique("gradient", new TechniqueDescriptor("gradient"));
			engine.DefineTechnique("blit", new TechniqueDescriptor("blit", LinearFilter: true));
			// Two passes: render a gradient into an offscreen target, then sample it into the backbuffer. Exercises
			// the executor's transient allocation + the COLOR_ATTACHMENT->SHADER_READ barrier + the SRV/sampler path.
			engine.SetGraph(RenderGraph.Define(g =>
			{
				var offscreen = g.Screen.Color("offscreen", Format.Rgba8Unorm);
				g.Pass("gradient").Writes(offscreen).Draw(ctx => ctx.FullScreen("gradient"));
				g.Pass("blit").Samples(offscreen).Writes(g.Backbuffer).Draw(ctx => ctx.FullScreen("blit", offscreen));
				g.Present(g.Backbuffer);
			}));
			mode = "graph executor";
		}
		else
		{
			mode = RunGallery(engine, out activeDemo);
		}

		Console.WriteLine($"Loom DX12 {mode} on {engine.DeviceName} ({engine.BackendName}). Close the window to exit.");

		// Input + the Dear ImGui HUD (§6/§9), the same control panel Program.cs builds on Vulkan — proving the
		// DX12 ImGui renderer end-to-end. The host owns the context + input; the engine's renderer does the GPU
		// work. Compiled out entirely when built without ImGui (UseImGui=false); the hud pass's ctx.DrawImGui()
		// is then a no-op, so the gallery still renders, just without the overlay.
		using var input = window.CreateInput();
		var mouse = input.Mice.Count > 0 ? input.Mice[0] : null;
		var fpsTimer = 0f;
		var fpsFrames = 0;
		var displayFps = 0.0;
		var displayMs = 0.0;
#if LOOM_IMGUI
		ImGui.CreateContext();
		var io = ImGui.GetIO();
		unsafe { io.NativePtr->IniFilename = null; } // don't persist a layout .ini next to the exe
		io.DisplaySize = new Vector2(window.FramebufferSize.X, window.FramebufferSize.Y);
		io.DisplayFramebufferScale = Vector2.One;
		io.Fonts.AddFontDefault();
		ImGui.StyleColorsDark();
		engine.EnableImGui();
		var showDemoWindow = false;
#endif

		// The sun: a directional light, constant — handed in every frame for the demos that read ctx.SunView
		// (Shadows / Full). Neutral right-handed projection; the DX12 backend ingests it as-is (no Vulkan Y-flip).
		var lightDir = Vector3.Normalize(new Vector3(0.45f, 0.55f, -0.6f));
		var sunCamera = new Camera(
			Matrix4x4.CreateLookAt(lightDir * 7f, Vector3.Zero, Vector3.UnitY),
			Matrix4x4.CreateOrthographic(9f, 9f, 0.1f, 14f));

		var frame = 0;
		var elapsed = 0f;
		window.FramebufferResize += size => engine.Resize(size.X, size.Y);
		window.Render += delta =>
		{
			frame++;
			elapsed += (float)delta;

			// Orbit the camera around the cube. Neutral right-handed, +Y-up projection — the DX12 backend ingests
			// it as-is (no Y-flip, unlike Vulkan), since D3D12 clip space is already +Y-up (Camera/M7-B5).
			// When capturing, FREEZE the orbit at a fixed angle so DX12 and Vulkan grab the identical viewpoint
			// (a deterministic A/B for pixel-diffing — see LOOM_CAPTURE).
			// Deterministic capture: freeze the orbit AND the scene time + frame delta (matching Program.cs), so the
			// two backends grab the identical viewpoint, particle phase, and delta-driven auto-exposure convergence.
			const float captureTime = 1.0f;
			const double captureDelta = 1.0 / 60.0;
			var camTime = capturePath is not null ? 0.6f : elapsed;
			var eye = new Vector3(MathF.Sin(camTime) * 2.5f, 1.6f, MathF.Cos(camTime) * 2.5f);
			var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
			var aspect = window.FramebufferSize.Y == 0 ? 1f : (float)window.FramebufferSize.X / window.FramebufferSize.Y;
			var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, aspect, 0.1f, 100f);
			var camera = new Camera(view, projection);

			// A demo may supply its own camera (the 2D demos' orthographic view) — honour it exactly as the Vulkan
			// host does (Program.cs). Without this the DX12 gallery renders the 2D sprite demos through the default
			// perspective orbit camera, so only the sprites near the world origin land in the frustum (the rest get a
			// negative clip.w and are culled) — the "DX12 renders almost nothing" symptom. Not a matrix bug.
			if (activeDemo?.GetCamera(aspect, camTime) is { } demoCamera)
				camera = demoCamera;

			// Per-frame CPU work for the active demo (the instancing demo's frustum cull) before Render.
			activeDemo?.Update(camera);

			// Picking (Highlighting / Full): sample the centre of the 320×180 pick domain — the cube sits there — to
			// exercise the GPU→CPU id readback; demos without a readback target return 0. In capture, force the cube
			// hovered (id 1) so the outline shows regardless of the few-frame readback latency (as Program.cs does).
			var picked = engine.SamplePick(160, 90);
			var hoverId = capturePath is not null ? 1u : (uint)MathF.Round(picked);

			// LOOM_CAPTURE=path grabs the frame to a PNG (a few frames in, so the swapchain has settled),
			// then exits — the headless pixel-level proof the scene actually renders.
			if (capturePath is not null && frame == 60)
				engine.RequestCapture(capturePath); // late enough that the delta-driven auto-exposure has converged

			fpsTimer += (float)delta;
			fpsFrames++;
			if (fpsTimer >= 0.25f)
			{
				displayFps = fpsFrames / fpsTimer;
				displayMs = fpsTimer / fpsFrames * 1000.0;
				fpsTimer = 0f;
				fpsFrames = 0;
			}

			// ImDrawData* for this frame; stays null (no UI) when built without ImGui — the hud pass's
			// ctx.DrawImGui() is then a no-op, so the gallery still renders, just without the overlay.
			nint imguiDrawData = 0;
#if LOOM_IMGUI
			io.DisplaySize = new Vector2(window.FramebufferSize.X, window.FramebufferSize.Y);
			io.DisplayFramebufferScale = Vector2.One;
			io.DeltaTime = delta > 0 ? (float)delta : 1f / 60f;
			if (mouse is not null)
			{
				io.AddMousePosEvent(mouse.Position.X, mouse.Position.Y);
				io.AddMouseButtonEvent(0, mouse.IsButtonPressed(MouseButton.Left));
				io.AddMouseButtonEvent(1, mouse.IsButtonPressed(MouseButton.Right));
				io.AddMouseWheelEvent(0f, mouse.ScrollWheels.Count > 0 ? mouse.ScrollWheels[0].Y : 0f);
			}

			ImGui.NewFrame();
			if (ImGui.BeginMainMenuBar())
			{
				if (ImGui.BeginMenu("Loom"))
				{
					ImGui.MenuItem($"{engine.BackendName} backend", "", false, false);
					ImGui.MenuItem("ImGui demo window", "", ref showDemoWindow);
					var gpuTiming = engine.GpuTimingEnabled;
					if (ImGui.MenuItem("GPU timing", "", ref gpuTiming))
						engine.GpuTimingEnabled = gpuTiming;
					ImGui.EndMenu();
				}
				ImGui.Separator();
				var status = $"{displayFps:F0} fps ({displayMs:F2} ms)   |   {mode}   |   hovered {hoverId}   |   {engine.DeviceName}";
				if (activeDemo?.HudStatus is { } demoStatus)
					status += $"   |   {demoStatus}";
				ImGui.Text(status);
				ImGui.EndMainMenuBar();
			}
			if (showDemoWindow)
				ImGui.ShowDemoWindow(ref showDemoWindow);
			if (engine.GpuTimingEnabled)
			{
				if (ImGui.Begin("GPU Timing"))
				{
					foreach (var t in engine.LastFrameTimings)
						ImGui.Text($"{t.Name,-18} {t.GpuMilliseconds,7:F3} ms");
					ImGui.Separator();
					ImGui.Text($"{"frame total",-18} {engine.LastFrameGpuMilliseconds,7:F3} ms");
				}
				ImGui.End();
			}
			ImGui.Render();
			unsafe { imguiDrawData = (nint)ImGui.GetDrawData().NativePtr; }
#endif

			engine.Render(new FrameInputs
			{
				DeltaSeconds = capturePath is not null ? captureDelta : delta,
				Time = capturePath is not null ? captureTime : elapsed,
				Camera = camera,
				SunView = sunCamera,
				HoverId = hoverId,
				ImGuiDrawData = imguiDrawData,
			});

			if (capturePath is not null && frame >= 62)
				window.Close();
			else if (smokeFrames > 0 && frame >= smokeFrames)
			{
				Console.WriteLine($"[loom] DX12 SamplePick(160,90) = {picked:F2} (the centred cube's object id ≈ 1 once readback settles).");
				if (engine.GpuTimingEnabled && engine.LastFrameTimings.Count > 0)
				{
					Console.WriteLine($"[loom] GPU timing ({engine.BackendName}, {mode}):");
					foreach (var t in engine.LastFrameTimings)
						Console.WriteLine($"  {t.Name,-18} {t.GpuMilliseconds,7:F3} ms");
					Console.WriteLine($"  {"frame total",-18} {engine.LastFrameGpuMilliseconds,7:F3} ms");
				}
				window.Close();
			}
		};

		window.Run();
		engine.WaitIdle();
#if LOOM_IMGUI
		ImGui.DestroyContext();
#endif
	}

	// Build + run the per-capability demo gallery (the same IDemo classes Program.cs runs on Vulkan), selecting
	// one by LOOM_DEMO=<name|index>. Every demo's techniques are registered once (idempotent across demos that
	// share one) and every graph is built; only the selected graph is made active. Returns the selected demo's
	// name for the status line. Demos that exercise a not-yet-ported DX12 capability (Tonemapping's persistent
	// storage image; Highlighting/Full's readback) throw a clear NotSupportedException at SetGraph if selected.
	private static string RunGallery(IEngine engine, out IDemo? selectedDemo)
	{
		var (cubeVerts, cubeIndices) = Geometry.BuildCube();
		var (planeVerts, planeIndices) = Geometry.BuildPlane();
		var (quadVerts, quadIndices) = Geometry.BuildQuad();
		// The shared brick material (albedo + normal map), decoded from committed PNGs via Loom.Assets (M11).
		var assets = Path.Combine(AppContext.BaseDirectory, "assets");
		var brick = engine.CreateMaterial(
			engine.CreateTexture(Loom.Assets.ImageLoader.Load(Path.Combine(assets, "brick_albedo.png"), TextureFormat.Rgba8Srgb)),
			engine.CreateTexture(Loom.Assets.ImageLoader.Load(Path.Combine(assets, "brick_normal.png"), TextureFormat.Rgba8Unorm)));
		var scene = new DemoScene(engine.CreateMesh(cubeVerts, cubeIndices), engine.CreateMesh(planeVerts, planeIndices), engine.CreateMesh(quadVerts, quadIndices), brick);

		IDemo[] demos =
		[
			new CubeLightingDemo(),
			new ShadowsDemo(),
			new SsaoDemo(),
			new TonemappingDemo(),
			new BloomDemo(),
			new ParticlesDemo(),
			new HighlightingDemo(),
			new InstancingDemo(),
			new SpritesDemo(),
			new Lighting2DDemo(),
			new SpriteAnimationDemo(),
			new TilemapDemo(),
			new FullReferenceDemo(),
			new NormalMappingDemo(),
			new GltfDemo(),
		];
		// Register each demo's techniques, skipping any whose shaders have no DX12 DXIL on this toolchain (the
		// build emits per-stage DXIL best-effort — e.g. ssao_blur's fragment stage doesn't cross-compile on
		// win-arm64). DefineTechnique throws NotSupportedException for the missing stage; drop that demo from the
		// gallery rather than crashing the whole harness at startup (the same spirit as the SetGraph-time guards).
		var live = new List<IDemo>();
		foreach (var demo in demos)
		{
			try
			{
				demo.DefineTechniques(engine);
				live.Add(demo);
			}
			catch (NotSupportedException ex)
			{
				Console.WriteLine($"[loom] DX12: skipping demo '{demo.Name}' — {ex.Message}");
			}
		}
		var galleryDemos = live.ToArray();
		var graphs = galleryDemos.Select(d => d.Build(scene)).ToArray();

		var selected = 0;
		var demoEnv = Environment.GetEnvironmentVariable("LOOM_DEMO");
		if (demoEnv is not null)
		{
			var byName = Array.FindIndex(galleryDemos, d => string.Equals(d.Name, demoEnv, StringComparison.OrdinalIgnoreCase));
			if (byName >= 0)
				selected = byName;
			else if (int.TryParse(demoEnv, out var byIndex) && byIndex >= 0 && byIndex < galleryDemos.Length)
				selected = byIndex;
			else
				Console.WriteLine($"[loom] LOOM_DEMO='{demoEnv}' is not a known/available demo ({string.Join(", ", galleryDemos.Select(d => d.Name))}); starting on {galleryDemos[selected].Name}.");
		}

		engine.SetGraph(graphs[selected]);
		selectedDemo = galleryDemos[selected];
		return $"demo: {galleryDemos[selected].Name}";
	}
}
#endif
