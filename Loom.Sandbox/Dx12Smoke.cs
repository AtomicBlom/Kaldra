#if LOOM_DX12
using System.Numerics;

using Loom.Core;
using Loom.DX12; // the one sanctioned backend type the host names; constructing DX12EngineSettings self-registers it

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

		Console.WriteLine($"Loom DX12 first slice on {engine.DeviceName} ({engine.BackendName}). Close the window to exit.");

		var frame = 0;
		var elapsed = 0f;
		window.FramebufferResize += size => engine.Resize(size.X, size.Y);
		window.Render += delta =>
		{
			frame++;
			elapsed += (float)delta;

			// LOOM_CAPTURE=path grabs the triangle to a PNG (a few frames in, so the swapchain has settled),
			// then exits — the headless pixel-level proof the triangle actually renders.
			if (capturePath is not null && frame == 3)
				engine.RequestCapture(capturePath);

			engine.Render(new FrameInputs { DeltaSeconds = delta, Time = elapsed });

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
