using System.Numerics;

using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Diagnostics;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Core.Primitives;
using Loom.UI.SUIsei;

namespace Loom.Tests;

/// <summary>
/// <see cref="IEngine.ReleaseTexture"/> lets an image live shorter than the engine — a UI image replaced when its source
/// changes, a level's art. What can go wrong is invisible in any one frame: the image is destroyed while a frame in
/// flight still samples it (a validation error or device fault a frame or two later), or something keyed by its view —
/// a cached descriptor set or bind group — outlives it and binds the destroyed view to whatever inherits its handle.
/// So this swaps the texture a draw list samples across frames, releasing each predecessor while frames are in flight
/// and some textures before any frame has drawn them (their uploads still pending), and asserts the frame shows the
/// newest texture, the validation layers stay quiet, and a stale handle is refused by name. The draw-list path is the
/// one that matters: a material pins its textures, so it is the only way an app samples a texture it later releases.
/// GPU-gated — SKIPPED when no backend is available. The WebGPU backend's half — its recycled view handles — is pinned
/// on the CPU by <see cref="BindingCacheTests"/>.
/// </summary>
[NotInParallel] // one GPU engine at a time, and LoomLog.Sink is process-wide
public class TextureReleaseTests
{
	private const int W = 64, H = 64;

	private static readonly byte[] Red = [255, 0, 0, 255];
	private static readonly byte[] Green = [0, 255, 0, 255];
	private static readonly byte[] Blue = [0, 0, 255, 255];

	[Test]
	public void A_released_texture_stops_drawing_and_its_replacement_draws()
	{
		foreach (var backend in AvailableBackends())
		{
			var errors = CaptureErrors(out var restore);
			byte[] centre;
			try
			{
				// Scoped so the engine is disposed inside the capture: DirectX12 reports its debug-layer tally on teardown.
				using (var scene = new Scene(backend))
				{
					// Churn: each round replaces the texture the pass samples and renders a frame or two, so releases land
					// while earlier frames are in flight. Every third round also releases a texture no frame has drawn,
					// whose upload is still waiting for the next frame's submission.
					var current = scene.Upload(Red);
					for (var round = 0; round < 12; round++)
					{
						scene.Drawn = current;
						scene.Render(frames: 1 + (round % 2));

						if (round % 3 == 0)
							scene.Engine.ReleaseTexture(scene.Upload(Blue));

						var next = scene.Upload(round % 2 == 0 ? Green : Red);
						scene.Drawn = next;
						scene.Engine.ReleaseTexture(current);
						current = next;
					}

					var final = scene.Upload(Blue);
					scene.Drawn = final;
					scene.Engine.ReleaseTexture(current);
					centre = scene.CaptureCentre();
				}
			}
			finally
			{
				restore();
			}

			centre.ShouldBe(Blue, $"{backend.Name}: the frame should show the newest texture (blue) — another colour is a released texture still being sampled, or the replacement's upload not yet arrived.");
			errors.ShouldBeEmpty($"{backend.Name}: releasing textures across frames in flight should leave the validation layer silent.");
		}
	}

	[Test]
	public void A_released_handle_is_refused()
	{
		foreach (var backend in AvailableBackends())
		{
			using var scene = new Scene(backend);
			var texture = scene.Upload(Green);
			scene.Drawn = texture;
			scene.Render(frames: 2);

			scene.Engine.ReleaseTexture(texture);

			// Releasing it twice would free it twice; that is the caller holding a dead handle, so it names it.
			var twice = Should.Throw<Exception>(() => scene.Engine.ReleaseTexture(texture));
			twice.Message.ShouldContain("released", customMessage: $"{backend.Name}: releasing a texture twice should say it was released.");

			// Sampling it would read memory the engine is about to free, or already has.
			var drawn = Should.Throw<Exception>(() => scene.Render(frames: 1));
			drawn.ToString().ShouldContain("released", customMessage: $"{backend.Name}: sampling a released texture should say it was released.");
		}
	}

	[Test]
	public void An_unassigned_texture_handle_is_refused()
	{
		foreach (var backend in AvailableBackends())
		{
			// Texture ids start at 1, so a Texture field nobody assigned names nothing rather than the first upload.
			using var scene = new Scene(backend);
			scene.Drawn = default;
			var drawn = Should.Throw<Exception>(() => scene.Render(frames: 1));
			drawn.ToString().ShouldContain("default(Texture)", customMessage: $"{backend.Name}: sampling default(Texture) should say the handle was never assigned.");
		}
	}

	[Test]
	public void A_texture_a_material_holds_cannot_be_released()
	{
		foreach (var backend in AvailableBackends())
		{
			using var scene = new Scene(backend);
			var texture = scene.Upload(Red);
			var material = scene.Engine.CreateMaterial(texture);

			// A material keeps its textures as long as it lives — the engine's lifetime — and every backend caches what a
			// material binds, so letting the texture go would leave that cache naming a destroyed image.
			var refused = Should.Throw<InvalidOperationException>(() => scene.Engine.ReleaseTexture(texture));
			refused.Message.ShouldContain($"material {material.Id}", customMessage: $"{backend.Name}: the refusal should name the material holding the texture.");

			// Refused means untouched: the texture still draws.
			scene.Drawn = texture;
			scene.CaptureCentre().ShouldBe(Red, $"{backend.Name}: a texture whose release was refused should still draw.");
		}
	}

	[Test]
	public void Creating_or_releasing_inside_a_pass_throws()
	{
		foreach (var backend in AvailableBackends())
		{
			using var scene = new Scene(backend);
			var engine = scene.Engine;
			var drawn = scene.Upload(Green);
			var spareTexture = scene.Upload(Red);
			var spareMesh = engine.CreateMesh([default, default, default], [0, 1, 2]);
			var data = Solid(Blue);
			scene.Drawn = drawn;

			// Every create and release, attempted from inside the pass callback on the render thread. Each is refused there
			// — an upload made mid-recording reaches the GPU only with the next frame, and a release could free what this
			// frame already draws with — while a background thread queuing an upload is exactly what the async overloads
			// are for, so it is let through.
			var refusals = new List<(string Verb, Exception? Thrown)>();
			Task<Texture>? queuedOffThread = null;
			scene.DuringPass = () =>
			{
				refusals.Add(("CreateTexture", Catch(() => engine.CreateTexture(data))));
				refusals.Add(("CreateTextureAsync", Catch(() => engine.CreateTextureAsync(data))));
				refusals.Add(("ReleaseTexture", Catch(() => engine.ReleaseTexture(spareTexture))));
				refusals.Add(("CreateMesh", Catch(() => engine.CreateMesh([default, default, default], [0, 1, 2]))));
				refusals.Add(("ReleaseMesh", Catch(() => engine.ReleaseMesh(spareMesh))));
				var background = new Thread(() => queuedOffThread = engine.CreateTextureAsync(data));
				background.Start();
				background.Join();
			};
			scene.Render(frames: 1);
			scene.DuringPass = null;

			foreach (var (verb, thrown) in refusals)
			{
				var refused = thrown.ShouldBeOfType<InvalidOperationException>($"{backend.Name}: {verb} inside a pass should be refused.");
				refused.Message.ShouldContain("pass 'list'", customMessage: $"{backend.Name}: the refusal of {verb} should name the recording pass.");
			}

			// Refused means untouched: both spares are still live, so releasing them between frames succeeds, and the
			// upload queued off the render thread completes with the next frame.
			engine.ReleaseTexture(spareTexture);
			engine.ReleaseMesh(spareMesh);
			scene.CaptureCentre().ShouldBe(Green, $"{backend.Name}: the frame whose pass refused the calls should still draw.");
			queuedOffThread.ShouldNotBeNull();
			queuedOffThread.IsCompletedSuccessfully.ShouldBeTrue($"{backend.Name}: an upload queued from another thread while a frame recorded should complete with the next frame.");
		}
	}

	private static Exception? Catch(Action action)
	{
		try
		{
			action();
			return null;
		}
		catch (Exception e)
		{
			return e;
		}
	}

	private static List<IRenderBackend> AvailableBackends()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");
		return [.. Backends.Available];
	}

	// Collect what the driver's debug layer reports as an error until the returned action restores the previous sink.
	private static List<string> CaptureErrors(out Action restore)
	{
		var errors = new List<string>();
		var previous = LoomLog.Sink;
		LoomLog.Sink = (level, message) =>
		{
			if (level == LoomLogLevel.Error)
				lock (errors) errors.Add(message);
		};
		restore = () => LoomLog.Sink = previous;
		return errors;
	}

	private static TextureData Solid(byte[] rgba) =>
		TextureData.FromRgba8(4, 4, [.. Enumerable.Repeat(rgba, 16).SelectMany(texel => texel)], TextureFormat.Rgba8Unorm, generateMips: false);

	// An offscreen engine whose one pass draws a quad sampling whichever texture Drawn names this frame, through the
	// draw-list verb the UI painter uses — the pass reads it live, the way a painter holds the image it currently shows.
	private sealed class Scene : IDisposable
	{
		private const int Inset = 8;

		public IEngine Engine { get; }
		public Texture Drawn { get; set; }

		/// <summary>Run inside the pass callback this frame, for probing what the engine allows while recording.</summary>
		public Action? DuringPass { get; set; }

		private readonly OverlayVertex[] _quad;
		private readonly ushort[] _indices = [0, 1, 2, 0, 2, 3];
		private readonly DrawCommand[] _command;
		private readonly TextureRef[] _bound = new TextureRef[1];

		public Scene(IRenderBackend backend)
		{
			Engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
			// The painter is created only for the "ui" technique it defines; nothing here draws a UI host.
			UIPainter.Create(Engine, new global::SUIsei.UIHost());

			var white = new Half4((Half)1f, (Half)1f, (Half)1f, (Half)1f);
			_quad =
			[
				new(new Vector2(Inset, Inset), new Vector2(0, 0), white, Vector4.Zero),
				new(new Vector2(W - Inset, Inset), new Vector2(1, 0), white, Vector4.Zero),
				new(new Vector2(W - Inset, H - Inset), new Vector2(1, 1), white, Vector4.Zero),
				new(new Vector2(Inset, H - Inset), new Vector2(0, 1), white, Vector4.Zero),
			];
			_command = [new DrawCommand(new Rect(0, 0, W, H), TextureIndex: 0, IndexOffset: 0, IndexCount: 6, VertexOffset: 0, DrawListGeometry.KindImage)];

			Engine.SetGraph(RenderGraph.Define(g =>
			{
				var screen = g.Backbuffer;
				g.Pass("list").Writes(screen).Draw(this, static (ctx, scene) => scene.Record(ctx));
				g.Present(screen);
			}));
		}

		public Texture Upload(byte[] rgba) => Engine.CreateTexture(Solid(rgba));

		public void Render(int frames)
		{
			var inputs = new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 };
			for (var frame = 0; frame < frames; frame++)
				Engine.Render(inputs);
		}

		// The centre texel of a capture of the next frame.
		public byte[] CaptureCentre()
		{
			var path = Path.Combine(Path.GetTempPath(), $"loom-texture-release-{Guid.NewGuid():N}.png");
			try
			{
				Engine.RequestCapture(path);
				Render(frames: 3);
				var (width, _, rgba) = PngReader.Read(path);
				var i = (((H / 2) * width) + (W / 2)) * 4;
				return rgba[i..(i + 4)];
			}
			finally
			{
				if (File.Exists(path))
					File.Delete(path);
			}
		}

		public void Dispose() => Engine.Dispose();

		private void Record(IRenderContext ctx)
		{
			DuringPass?.Invoke();
			_bound[0] = Drawn;
			// surface pixels → clip, paper white 1, SDR.
			Span<float> push = [2f / W, 2f / H, -1f, -1f, 1f, 0f];
			ctx.DrawList("ui", DrawListIdentity.Transient, _bound, _quad, _indices, _command, push);
		}
	}
}
