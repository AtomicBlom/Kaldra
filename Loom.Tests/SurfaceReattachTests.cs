using Loom.Backend.Vulkan;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;

namespace Loom.Tests;

/// <summary>
/// <see cref="IReattachableSurface"/> on the Vulkan backend, on a real window surface: what Android does to an app
/// that leaves the screen (its surface is destroyed, a new one comes when it returns), done here with a hidden desktop
/// window, so the swapchain teardown and rebuild run on a desktop GPU before they run on a phone. GPU-gated: SKIPPED
/// when Vulkan is not usable on this box.
/// </summary>
[NotInParallel] // brings a device up and down; concurrent bring-up is not something the backends survive
public class SurfaceReattachTests
{
	private const int Width = 256;
	private const int Height = 192;

	[Test]
	public void A_detached_engine_keeps_its_resources_and_renders_again_on_a_new_surface()
	{
		_ = new VulkanEngineSettings(); // load the backend so it self-registers (behind its own IsSupported probe)
		if (!Backends.Available.Any(b => b.Name == "Vulkan"))
			throw new TUnit.Core.Exceptions.SkipTestException("Vulkan is not available on this box.");

		GlfwWindowing.RegisterPlatform();
		using var window = Window.Create(WindowOptions.DefaultVulkan with
		{
			Size = new Vector2D<int>(Width, Height),
			IsVisible = false,
			Title = "Loom surface reattach test",
		});
		window.Initialize();
		var surface = window.VkSurface ?? throw new InvalidOperationException("The test window exposes no Vulkan surface.");

		using var engine = new VulkanBackend().Create(surface, window.FramebufferSize.X, window.FramebufferSize.Y, new VulkanEngineSettings());
		var reattachable = engine.ShouldBeAssignableTo<IReattachableSurface>()!;
		reattachable.HasSurface.ShouldBeTrue();

		// Something to keep across the detach: a technique (and so a pipeline) and the graph using it.
		engine.DefineTechnique("fullscreen", new TechniqueDescriptor("fullscreen"));
		engine.SetGraph(RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			g.Pass("fill").Writes(screen).Draw(static ctx => ctx.FullScreen("fullscreen"));
			g.Present(screen);
		}));
		var inputs = new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 };
		for (var frame = 0; frame < 3; frame++)
			engine.Render(inputs);

		reattachable.DetachSurface();
		reattachable.HasSurface.ShouldBeFalse();
		reattachable.DetachSurface(); // a second detach does nothing
		Should.Throw<InvalidOperationException>(() => engine.Render(inputs));
		engine.Resize(Width, Height); // a resize while detached is only remembered

		// The same window makes a new VkSurfaceKHR, as Android hands over a new surface for the same activity.
		reattachable.AttachSurface(surface, window.FramebufferSize.X, window.FramebufferSize.Y);
		reattachable.HasSurface.ShouldBeTrue();
		Should.Throw<InvalidOperationException>(() => reattachable.AttachSurface(surface, Width, Height));

		var capturePath = Path.Combine(Path.GetTempPath(), $"loom-reattach-{Guid.NewGuid():N}.png");
		try
		{
			for (var frame = 0; frame < 3; frame++)
			{
				if (frame == 2)
					engine.RequestCapture(capturePath);
				engine.Render(inputs);
			}

			File.Exists(capturePath).ShouldBeTrue("RequestCapture after the reattach produced no file.");
			var (w, h, rgba) = PngReader.Read(capturePath);
			w.ShouldBe(window.FramebufferSize.X);
			h.ShouldBe(window.FramebufferSize.Y);
			// Two flat frames would compare equal to anything: the fullscreen gradient must actually be there.
			var distinct = new HashSet<int>();
			for (var i = 0; i < rgba.Length; i += 4)
				distinct.Add(rgba[i] | (rgba[i + 1] << 8) | (rgba[i + 2] << 16));
			distinct.Count.ShouldBeGreaterThan(16, "The frame after the reattach is flat: nothing was drawn to the new surface.");
		}
		finally
		{
			File.Delete(capturePath);
		}

		engine.WaitIdle();
	}
}
