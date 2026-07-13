using Loom.Backend.Vulkan;
using Loom.Core.Hosting;
using Loom.Core.Materials;

using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace HelloTriangle;

/// <summary>
/// Desktop bootstrap: create a Vulkan-surfaced window, bring up the engine, submit one frame per
/// <see cref="IWindow.Render"/> tick. Deliberately does none of Loom.Sandbox's arg parsing, demo gallery, or
/// backend choice — this spike proves the RID/build mechanism, not the host-loop unification.
/// </summary>
internal static class Program
{
	private static void Main()
	{
		var window = Window.Create(WindowOptions.DefaultVulkan with
		{
			Size = new Vector2D<int>(1280, 720),
			Title = "Loom - Hello Triangle (Vulkan)",
		});
		window.Initialize();

		if (window.VkSurface is null)
			throw new InvalidOperationException("The windowing platform did not expose a Vulkan surface.");

		var settings = new VulkanEngineSettings();
		var backend = Engine.AvailableEngines.First();
		var engine = backend.Create(window.VkSurface, window.FramebufferSize.X, window.FramebufferSize.Y, settings);

		engine.DefineTechnique("triangle", new TechniqueDescriptor("triangle"));
		engine.SetGraph(TriangleGraph.Build());

		window.FramebufferResize += size => engine.Resize(size.X, size.Y);

		// Minimal headless verification hook: HELLOTRIANGLE_CAPTURE=<path> captures frame 3, then closes the
		// window — no LaunchOptions-style CLI parser needed for a one-technique spike.
		var capturePath = Environment.GetEnvironmentVariable("HELLOTRIANGLE_CAPTURE");
		var totalTime = 0f;
		var frame = 0;
		window.Render += delta =>
		{
			totalTime += (float)delta;
			engine.Render(new FrameInputs { DeltaSeconds = delta, Time = totalTime });
			frame++;
			if (capturePath is { Length: > 0 })
			{
				if (frame == 3)
					engine.RequestCapture(capturePath);
				else if (frame == 4)
					window.Close();
			}
		};

		window.Run();
		engine.Dispose();
	}
}
