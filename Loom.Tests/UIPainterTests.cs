using System.Numerics;

using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Primitives;
using Loom.UI.SUIsei;
using SUIsei;
using SUIsei.Controls;
using SUIsei.Drawing;
using SUIsei.Media;
using SUIsei.Media.Imaging;

namespace Loom.Tests;

/// <summary>
/// The UI painter's frame contract, end to end on each backend. Its <see cref="UIPainter.Prepare"/> step exists because
/// <see cref="UIPainter.Draw"/> runs inside a pass, where an image cannot be uploaded in time for the frame drawing it;
/// and it keeps each surface's geometry under an engine-minted draw-list stream, because every <see cref="UIHost"/>
/// numbers its surfaces from zero. These pin the three ways that goes wrong: an image missing from its first frame, a
/// frame that skipped <see cref="UIPainter.Prepare"/> drawing anyway, and two hosts in one frame colliding on stream 0.
/// GPU-gated — SKIPPED when no backend is available.
/// </summary>
[NotInParallel] // one GPU engine at a time
public class UIPainterTests
{
	private const int W = 64, H = 64;
	private static readonly FrameInputs Inputs = new() { Time = 0, DeltaSeconds = 1.0 / 60.0 };

	[Test]
	public void Two_hosts_draw_in_one_frame()
	{
		foreach (var backend in AvailableBackends())
		{
			// A HUD and an in-world panel are two hosts, and each numbers its first surface 0. Kept geometry is per stream,
			// and both lists are at their first revision, so if both submitted one stream the second would be served the
			// first's kept buffers. Each painter mints its surfaces' streams, so they never share one.
			using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
			var left = new Hosted(engine, HorizontalAlignment.Left, HdrColor.FromRgb(255, 0, 0));
			var right = new Hosted(engine, HorizontalAlignment.Right, HdrColor.FromRgb(0, 0, 255));
			left.Surface.ShouldBe(right.Surface, "the test needs the two hosts' surface ids to collide, as every host's first surface does");
			engine.SetGraph(RenderGraph.Define(g =>
			{
				var screen = g.Backbuffer;
				g.Pass("ui").Writes(screen).Draw(ctx =>
				{
					left.Painter.Draw(ctx, left.Surface);
					right.Painter.Draw(ctx, right.Surface);
				});
				g.Present(screen);
			}));

			var (width, rgba) = Capture(engine, frames: 4, frame =>
			{
				left.Compose();
				right.Compose();
			});

			Pixel(rgba, width, W / 4, H / 2).ShouldBe(Colour.Red, $"{backend.Name}: the left host's half should be red.");
			Pixel(rgba, width, W * 3 / 4, H / 2).ShouldBe(Colour.Blue, $"{backend.Name}: the right host's half should be blue.");
		}
	}

	[Test]
	public void Drawing_a_frame_that_skipped_Prepare_throws()
	{
		foreach (var backend in AvailableBackends())
		{
			using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
			var hosted = new Hosted(engine, HorizontalAlignment.Stretch, HdrColor.FromRgb(0, 255, 0));
			engine.SetGraph(RenderGraph.Define(g =>
			{
				var screen = g.Backbuffer;
				g.Pass("ui").Writes(screen).Draw(ctx => hosted.Painter.Draw(ctx, hosted.Surface));
				g.Present(screen);
			}));

			hosted.Compose();
			engine.Render(Inputs);

			// The UI has not changed, so nothing would go wrong this frame — which is exactly why the omission must be
			// caught now: otherwise it surfaces only on the frame the UI first shows a new image.
			hosted.Host.Tick(1.0 / 60.0, 0f, new Vector2(W, H));
			hosted.Host.Render();
			var thrown = Should.Throw<Exception>(() => engine.Render(Inputs));
			thrown.ToString().ShouldContain("UIPainter.Prepare", customMessage: $"{backend.Name}: drawing an unprepared frame should name the missing step.");
		}
	}

	[Test]
	public void A_prepared_surface_draws_in_every_pass_of_its_frame()
	{
		foreach (var backend in AvailableBackends())
		{
			// One UI shown twice in a frame — on the frame and again into an inset — is one Prepare and two Draws. The
			// check that catches a skipped Prepare must not take the second Draw for one.
			using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
			var hosted = new Hosted(engine, HorizontalAlignment.Stretch, HdrColor.FromRgb(0, 255, 0));
			engine.SetGraph(RenderGraph.Define(g =>
			{
				var inset = g.Domain("inset", W, H).Color("inset", Format.Rgba8Srgb);
				g.Pass("inset").Writes(inset).Draw(ctx => hosted.Painter.Draw(ctx, hosted.Surface));
				var screen = g.Backbuffer;
				g.Pass("ui").Samples(inset).Writes(screen).Draw(ctx =>
				{
					hosted.Painter.Draw(ctx, hosted.Surface);
					hosted.Painter.Draw(ctx, hosted.Surface);
				});
				g.Present(screen);
			}));

			var (width, rgba) = Capture(engine, frames: 3, _ => hosted.Compose());
			Pixel(rgba, width, W / 2, H / 2).ShouldBe(Colour.Green, $"{backend.Name}: the surface should draw on the frame.");

			// The next frame skips Prepare, and its first Draw still says so.
			hosted.Host.Tick(1.0 / 60.0, 0f, new Vector2(W, H));
			hosted.Host.Render();
			Should.Throw<Exception>(() => engine.Render(Inputs)).ToString()
				.ShouldContain("UIPainter.Prepare", customMessage: $"{backend.Name}: a frame that skipped Prepare should still be refused.");
		}
	}

	[Test]
	public void A_stream_submitted_again_in_one_frame_must_hold_the_same_revision()
	{
		foreach (var backend in AvailableBackends())
		{
			// A stable stream keeps one buffer per frame slot. Submitted again at the revision it holds, it re-binds the same
			// bytes; at another, the second write would land before the first draw executes and serve both its geometry.
			using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
			UIPainter.Create(engine, new UIHost()); // for the "ui" technique
			var stream = engine.AllocateDrawListStream();
			var white = new Half4((Half)1f, (Half)1f, (Half)1f, (Half)1f);
			Loom.Core.Geometry.OverlayVertex[] quad =
			[
				new(new Vector2(0, 0), Vector2.Zero, white, Vector4.Zero),
				new(new Vector2(W, 0), Vector2.Zero, white, Vector4.Zero),
				new(new Vector2(W, H), Vector2.Zero, white, Vector4.Zero),
			];
			ushort[] indices = [0, 1, 2];
			Loom.Core.Hosting.DrawCommand[] commands = [new(new Loom.Core.Primitives.Rect(0, 0, W, H), TextureIndex: 0, IndexOffset: 0, IndexCount: 3, VertexOffset: 0, Tag: 0)];
			TextureRef[] textures = [engine.CreateTexture(Loom.Core.Materials.TextureData.FromRgba8(1, 1, [255, 255, 255, 255], Loom.Core.Materials.TextureFormat.Rgba8Unorm, generateMips: false))];
			var secondRevision = 1u;
			engine.SetGraph(RenderGraph.Define(g =>
			{
				var screen = g.Backbuffer;
				g.Pass("ui").Writes(screen).Draw(ctx =>
				{
					Span<float> push = [2f / W, 2f / H, -1f, -1f, 1f, 0f];
					ctx.DrawList("ui", new DrawListIdentity(stream, 1), textures, quad, indices, commands, push);
					ctx.DrawList("ui", new DrawListIdentity(stream, secondRevision), textures, quad, indices, commands, push);
				});
				g.Present(screen);
			}));

			Should.NotThrow(() => engine.Render(Inputs), $"{backend.Name}: the same revision twice in a frame should draw.");
			secondRevision = 2;
			Should.Throw<Exception>(() => engine.Render(Inputs)).ToString()
				.ShouldContain("revisions 1 and 2", customMessage: $"{backend.Name}: two revisions of one stream in a frame should be refused.");
		}
	}

	[Test]
	public void An_image_draws_on_its_first_frame()
	{
		foreach (var backend in AvailableBackends())
		{
			// The image's texture is created by Prepare, before the frame records, so its upload is submitted ahead of the
			// very frame that samples it. Created from inside the pass instead, the first frame would sample an image its
			// pixels had not reached.
			using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
			var host = new UIHost();
			var view = host.CreateRoot(UILayer.Default);
			var red = Enumerable.Repeat<byte[]>([255, 0, 0, 255], 16).SelectMany(texel => texel).ToArray();
			view.Root.Content = new Image { Source = BitmapSource.FromRgba8(4, 4, red), Stretch = Stretch.Fill, Width = W, Height = H };
			var painter = UIPainter.Create(engine, host);
			engine.SetGraph(RenderGraph.Define(g =>
			{
				var screen = g.Backbuffer;
				g.Pass("ui").Writes(screen).Draw(ctx => painter.Draw(ctx, view.Surface));
				g.Present(screen);
			}));

			var (width, rgba) = Capture(engine, frames: 1, _ =>
			{
				host.Tick(1.0 / 60.0, 0f, new Vector2(W, H));
				host.Render();
				painter.Prepare();
			});

			Pixel(rgba, width, W / 2, H / 2).ShouldBe(Colour.Red, $"{backend.Name}: the image should be drawn on the first frame that shows it.");
		}
	}

	[Test]
	public void A_steady_UI_frame_allocates_nothing_in_the_painter()
	{
		const int WarmupFrames = 20, MeasuredFrames = 30;
		foreach (var backend in AvailableBackends())
		{
			// Text, a fill and an image, so every slot kind the painter resolves is on screen. Once the UI stops changing,
			// Prepare finds every surface at the revision it already resolved, and Draw re-submits the kept stream, so the
			// painter's whole frame — and the engine's draw of it — must allocate nothing.
			using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
			var host = new UIHost();
			var view = host.CreateRoot(UILayer.Default);
			var red = Enumerable.Repeat<byte[]>([255, 0, 0, 255], 16).SelectMany(texel => texel).ToArray();
			var panel = new StackPanel { Background = new SolidColorBrush(HdrColor.FromRgb(0, 0, 255)) };
			panel.Children.Add(new TextBlock { Text = "Steady", FontSize = 14f });
			panel.Children.Add(new Image { Source = BitmapSource.FromRgba8(4, 4, red), Stretch = Stretch.Fill, Width = 16, Height = 16 });
			view.Root.Content = panel;
			var painter = UIPainter.Create(engine, host);
			engine.SetGraph(RenderGraph.Define(g =>
			{
				var screen = g.Backbuffer;
				g.Pass("ui").Writes(screen).Draw((painter, view.Surface), static (ctx, ui) => ui.painter.Draw(ctx, ui.Surface));
				g.Present(screen);
			}));

			for (var frame = 0; frame < WarmupFrames; frame++)
			{
				host.Tick(1.0 / 60.0, 0f, new Vector2(W, H));
				host.Render();
				painter.Prepare();
				engine.Render(Inputs);
			}

			Allocations.ShouldAllocateNothing(() =>
			{
				for (var frame = 0; frame < MeasuredFrames; frame++)
				{
					painter.Prepare();
					engine.Render(Inputs);
				}
			}, $"{backend.Name}: the painter's steady frame allocated, in windows of {MeasuredFrames} frames.");
		}
	}

	[Test]
	public void An_image_two_surfaces_show_uploads_once_and_is_released_when_neither_does()
	{
		foreach (var backend in AvailableBackends())
		{
			using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
			var host = new UIHost();
			var first = host.CreateRoot(UILayer.Default);
			var second = host.CreateRoot(UILayer.Default);
			var image = BitmapSource.FromRgba8(4, 4, Solid(255, 0, 0));
			first.Root.Content = ImageElement(image);
			second.Root.Content = ImageElement(image);
			var painter = UIPainter.Create(engine, host);
			engine.SetGraph(TwoSurfaceGraph(painter, first.Surface, second.Surface));

			// Both surfaces name the image: it is uploaded once, and both bind that one texture. White is the only
			// other source either table names.
			Frames(engine, host, painter, 3);
			var shared = painter.ResidentTexture(image).ShouldNotBeNull($"{backend.Name}: the image should be uploaded.");
			painter.ResidentSourceCount.ShouldBe(2, $"{backend.Name}: white and the one image, uploaded once between them.");
			painter.SlotTextures(first.Surface).ToArray().ShouldContain(shared);
			painter.SlotTextures(second.Surface).ToArray().ShouldContain(shared);

			// One surface stops showing it: the other still does, so it stays.
			first.Root.Content = Fill(HdrColor.FromRgb(0, 255, 0));
			Frames(engine, host, painter, 2);
			painter.ResidentTexture(image).ShouldBe(shared, $"{backend.Name}: a source one surface still shows must not be released.");

			// Neither does: it is released — its handle is dead at once — and the frames already in flight that sampled
			// it finish safely, because the engine destroys it only after them.
			second.Root.Content = Fill(HdrColor.FromRgb(0, 0, 255));
			Frames(engine, host, painter, 4);
			painter.ResidentTexture(image).ShouldBeNull($"{backend.Name}: a source no surface shows should be released.");
			var refused = Should.Throw<Exception>(() => engine.ReleaseTexture(shared));
			refused.Message.ShouldContain("released", customMessage: $"{backend.Name}: the painter should have released the image's texture.");
		}
	}

	[Test]
	public void A_removed_surface_gives_up_its_textures()
	{
		foreach (var backend in AvailableBackends())
		{
			using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
			var host = new UIHost();
			var kept = host.CreateRoot(UILayer.Default);
			var removed = host.CreateRoot(UILayer.Default);
			var image = BitmapSource.FromRgba8(4, 4, Solid(255, 0, 0));
			kept.Root.Content = Fill(HdrColor.FromRgb(0, 255, 0));
			removed.Root.Content = ImageElement(image);
			var painter = UIPainter.Create(engine, host);
			engine.SetGraph(TwoSurfaceGraph(painter, kept.Surface, removed.Surface));

			Frames(engine, host, painter, 2);
			painter.ResidentTexture(image).ShouldNotBeNull();

			// The root goes, and with it the only surface naming the image: the host stops listing it, so nothing would
			// otherwise ever let its textures go.
			host.RemoveRoot(removed);
			Frames(engine, host, painter, 2);
			painter.ResidentTexture(image).ShouldBeNull($"{backend.Name}: a removed surface's sources should be released.");
		}
	}

	[Test]
	public void The_glyph_atlas_arrives_through_a_slot()
	{
		foreach (var backend in AvailableBackends())
		{
			// The painter is given no font: text samples whichever atlas page the compositor laid it out against, named in
			// the list's own slot table like any other source.
			using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
			var host = new UIHost();
			var view = host.CreateRoot(UILayer.Default);
			view.Root.Content = new TextBlock { Text = "Slots", FontSize = 16f };
			var painter = UIPainter.Create(engine, host);
			engine.SetGraph(RenderGraph.Define(g =>
			{
				var screen = g.Backbuffer;
				g.Pass("ui").Writes(screen).Draw(ctx => painter.Draw(ctx, view.Surface));
				g.Present(screen);
			}));

			Frames(engine, host, painter, 2);
			var slots = host.DrawLists[0].Data.TextureSlots;
			var page = slots.Single(source => source is AtlasImage);
			var slot = IndexOf(slots, page);
			var texture = painter.ResidentTexture(page).ShouldNotBeNull($"{backend.Name}: the atlas page should be uploaded.");
			painter.SlotTextures(view.Surface)[slot].ShouldBe(texture, $"{backend.Name}: the atlas page's slot should bind its texture.");
		}
	}

	private static int IndexOf(IReadOnlyList<TextureSource> slots, TextureSource source)
	{
		for (var i = 0; i < slots.Count; i++)
		{
			if (ReferenceEquals(slots[i], source))
				return i;
		}
		return -1;
	}

	private static byte[] Solid(byte r, byte g, byte b) => [.. Enumerable.Repeat<byte[]>([r, g, b, 255], 16).SelectMany(texel => texel)];

	private static Image ImageElement(ImageSource source) => new() { Source = source, Stretch = Stretch.Fill, Width = 16, Height = 16 };

	private static Border Fill(HdrColor colour) => new() { Width = 16, Height = 16, Background = new SolidColorBrush(colour) };

	// One pass drawing two surfaces of the same host, one after the other.
	private static RenderGraph TwoSurfaceGraph(UIPainter painter, DrawListId a, DrawListId b) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		g.Pass("ui").Writes(screen).Draw(ctx =>
		{
			painter.Draw(ctx, a);
			painter.Draw(ctx, b);
		});
		g.Present(screen);
	});

	private static void Frames(IEngine engine, UIHost host, UIPainter painter, int count)
	{
		for (var frame = 0; frame < count; frame++)
		{
			host.Tick(1.0 / 60.0, 0f, new Vector2(W, H));
			host.Render();
			painter.Prepare();
			engine.Render(Inputs);
		}
	}

	private enum Colour { Red, Green, Blue, Other }

	// Which primary a pixel is, with room for the UI's colour conversion; anything mixed or dark is Other.
	private static Colour Pixel(byte[] rgba, int width, int x, int y)
	{
		var i = ((y * width) + x) * 4;
		var (r, g, b) = (rgba[i], rgba[i + 1], rgba[i + 2]);
		return (r > 200, g > 200, b > 200, r < 50, g < 50, b < 50) switch
		{
			(true, false, false, _, true, true) => Colour.Red,
			(false, true, false, true, _, true) => Colour.Green,
			(false, false, true, true, true, _) => Colour.Blue,
			_ => Colour.Other,
		};
	}

	// Render frames, composing the UI before each, and capture the last.
	private static (int Width, byte[] Rgba) Capture(IEngine engine, int frames, Action<int> beforeFrame)
	{
		var path = Path.Combine(Path.GetTempPath(), $"loom-ui-painter-{Guid.NewGuid():N}.png");
		try
		{
			for (var frame = 1; frame <= frames; frame++)
			{
				beforeFrame(frame);
				if (frame == frames)
					engine.RequestCapture(path);
				engine.Render(Inputs);
			}
			var (width, _, rgba) = PngReader.Read(path);
			return (width, rgba);
		}
		finally
		{
			if (File.Exists(path))
				File.Delete(path);
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

	// One host with one surface: a full-height fill on one side (or across the whole surface), and its own painter.
	private sealed class Hosted
	{
		public UIHost Host { get; } = new();
		public UIPainter Painter { get; }
		public DrawListId Surface { get; }

		public Hosted(IEngine engine, HorizontalAlignment side, HdrColor colour)
		{
			var view = Host.CreateRoot(UILayer.Default);
			view.Root.Content = new Border
			{
				HorizontalAlignment = side,
				Width = side == HorizontalAlignment.Stretch ? W : W / 2,
				Height = H,
				Background = new SolidColorBrush(colour),
			};
			Surface = view.Surface;
			Painter = UIPainter.Create(engine, Host);
		}

		public void Compose()
		{
			Host.Tick(1.0 / 60.0, 0f, new Vector2(W, H));
			Host.Render();
			Painter.Prepare();
		}
	}
}
