using System.Numerics;
using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Diagnostics;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Core.Primitives;
using Loom.UI.SUIsei;
using SUIsei;
using SUIsei.Controls;
using SUIsei.Drawing;
using SUIsei.Extensions;
using SUIsei.Media;
using SUIsei.Media.Imaging;
using Ellipse = SUIsei.Shapes.Ellipse;
using LoomDrawCommand = Loom.Core.Hosting.DrawCommand;
using LoomRect = Loom.Core.Primitives.Rect;
using OverlayVertex = Loom.Core.Geometry.OverlayVertex;
using SoftwareRasterizer = SUIsei.Rasterizer.SoftwareRasterizer;

namespace Loom.Tests;

/// <summary>
/// Images through the painter read as their sources declare, as SUIsei's rasterizer reads them: an sRGB source decoded
/// per texel before filtering (the upload format's job), a straight-alpha one premultiplied after the filter (the
/// shader's, flagged per command by the painter), whether an <see cref="Image"/> or a shape's <see cref="ImageBrush"/>
/// samples it. Also the two kinds of image a host supplies: a <see cref="HostImageSource"/> bound to a graph texture by
/// <see cref="UIPainter.SetExternalTexture"/>, and a <see cref="WriteableBitmap"/> rewritten every frame. GPU-gated.
/// </summary>
[NotInParallel] // one GPU engine at a time, and LoomLog.Sink is process-wide
public class UIImageTests
{
	private const int W = 384, H = 128;
	private const int PerChannelTolerance = 4;
	private const double MaxFailedFraction = 0.005;
	private static readonly FrameInputs Inputs = new() { Time = 0, DeltaSeconds = 1.0 / 60.0 };
	private static readonly Vector4 Background = new(0.2f, 0.25f, 0.3f, 1f);

	[Test]
	public void Straight_and_premultiplied_images_render_as_SUIsei_s_rasterizer_does()
	{
		var backends = AvailableBackends();
		var failures = new List<string>();
		foreach (var backend in backends)
		{
			var path = Path.Combine(Path.GetTempPath(), $"loom-uiimages-{backend.Name}-{Guid.NewGuid():N}.png");
			var errors = CaptureErrors(out var previousSink);
			try
			{
				using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
				var host = new UIHost();
				var view = host.CreateRoot(UILayer.Default);
				view.Root.Content = ImagePage();
				var painter = UIPainter.Create(engine, host);
				engine.SetGraph(RenderGraph.Define(g =>
				{
					g.BackbufferClear = Background;
					var screen = g.Backbuffer;
					g.Pass("ui").Writes(screen).Draw((painter, view.Surface), static (ctx, ui) => ui.painter.Draw(ctx, ui.Surface));
					g.Present(screen);
				}));
				for (var frame = 1; frame <= 3; frame++)
				{
					Frame(host, painter);
					if (frame == 2)
						engine.RequestCapture(path);
					engine.Render(Inputs);
				}
				LoomLog.Sink = previousSink;
				foreach (var error in errors)
					failures.Add($"{backend.Name}: the debug layer reported: {error}");

				// The straight image's commands carry the flag, and the premultiplied one's do not.
				var commands = painter.PreparedCommands(view.Surface).ToArray();
				commands.Count(c => (c.Tag & 0x100) != 0).ShouldBe(2, $"{backend.Name}: the two straight images and the straight brush should be flagged once per draw run");

				// The oracle rasterizes the same list onto the same background.
				var list = host.DrawLists[0].Data;
				var oracle = new SoftwareRasterizer(W, H);
				oracle.Clear(Background);
				oracle.Render(list);
				oracle.SkippedCommands.ShouldBe(0);
				var expected = oracle.ResolveToRgba8();
				ImageComparison.DistinctColors(expected).ShouldBeGreaterThan(32, "the oracle's render of the images is near-empty");

				var (w, h, actual) = PngReader.Read(path);
				var diff = ImageComparison.Compare(w, h, actual, expected, PerChannelTolerance);
				if (diff.FailedFraction > MaxFailedFraction)
					failures.Add($"{backend.Name} vs the rasterizer: {diff}");
			}
			finally
			{
				LoomLog.Sink = previousSink;
				if (File.Exists(path)) File.Delete(path);
			}
		}

		failures.ShouldBeEmpty("Images read differently on the GPU and in SUIsei's rasterizer:\n  " + string.Join("\n  ", failures));
	}

	[Test]
	public void A_host_image_shows_the_graph_texture_bound_to_its_tag()
	{
		var failures = new List<string>();
		foreach (var backend in AvailableBackends())
		{
			var path = Path.Combine(Path.GetTempPath(), $"loom-hostimage-{backend.Name}-{Guid.NewGuid():N}.png");
			var errors = CaptureErrors(out var previousSink);
			try
			{
				RenderHostImage(backend, path, bind: true, declare: true, TextureColorSpace.Srgb);
				LoomLog.Sink = previousSink;
				foreach (var error in errors)
					failures.Add($"{backend.Name}: the debug layer reported: {error}");

				// The image fills 64x64 at the top left with the 8x8 target: each quadrant's centre is 16 px in.
				var (w, _, rgba) = PngReader.Read(path);
				Expect(failures, backend, rgba, w, 16, 16, (255, 0, 0));
				Expect(failures, backend, rgba, w, 48, 16, (0, 255, 0));
				Expect(failures, backend, rgba, w, 16, 48, (0, 0, 255));
				Expect(failures, backend, rgba, w, 48, 48, (255, 255, 255));
			}
			finally
			{
				LoomLog.Sink = previousSink;
				if (File.Exists(path)) File.Delete(path);
			}
		}

		failures.ShouldBeEmpty("A host image did not show its graph texture:\n  " + string.Join("\n  ", failures));
	}

	[Test]
	public void A_host_image_fails_loud_without_a_texture_a_matching_colour_space_or_a_declared_read()
	{
		foreach (var backend in AvailableBackends())
		{
			// No texture bound to the tag: Prepare names the tag rather than drawing nothing.
			Should.Throw<InvalidOperationException>(() => RenderHostImage(backend, null, bind: false, declare: true, TextureColorSpace.Srgb))
				.Message.ShouldContain("\"map\"");

			// A linear host image over an sRGB target would be decoded where the app said not to.
			Should.Throw<InvalidOperationException>(() => RenderHostImage(backend, null, bind: true, declare: true, TextureColorSpace.Linear))
				.Message.ShouldContain("Linear");

			// The UI pass must declare what it samples, like any pass.
			var thrown = Should.Throw<Exception>(() => RenderHostImage(backend, null, bind: true, declare: false, TextureColorSpace.Srgb));
			var binding = Unwrap<PassBindingException>(thrown);
			binding.ShouldNotBeNull($"{backend.Name}: expected a PassBindingException, got {thrown}");
			binding.Message.ShouldContain(".Samples(map)");
		}
	}

	[Test]
	public void A_WriteableBitmap_rewritten_every_frame_shows_each_frame_s_texels_and_keeps_one_texture()
	{
		foreach (var backend in AvailableBackends())
		{
			using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
			var host = new UIHost();
			var view = host.CreateRoot(UILayer.Default);
			var bitmap = new WriteableBitmap(2, 2);
			view.Root.Content = TopLeft(new Image { Source = bitmap, Stretch = Stretch.Fill, Width = 32, Height = 32 });
			var painter = UIPainter.Create(engine, host);
			engine.SetGraph(RenderGraph.Define(g =>
			{
				g.BackbufferClear = Background;
				var screen = g.Backbuffer;
				g.Pass("ui").Writes(screen).Draw((painter, view.Surface), static (ctx, ui) => ui.painter.Draw(ctx, ui.Surface));
				g.Present(screen);
			}));

			(byte R, byte G, byte B)[] colours = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0)];
			Texture? previous = null;
			for (var frame = 0; frame < colours.Length; frame++)
			{
				var (r, g, b) = colours[frame];
				Fill(bitmap.PixelBuffer.Span, r, g, b);
				bitmap.Invalidate();
				Frame(host, painter);
				var path = Path.Combine(Path.GetTempPath(), $"loom-writeable-{backend.Name}-{frame}-{Guid.NewGuid():N}.png");
				try
				{
					engine.RequestCapture(path);
					engine.Render(Inputs);
					Frame(host, painter); // the texels stay, so this frame replaces nothing
					engine.Render(Inputs); // a capture lands once its frame retires

					var texture = painter.ResidentTexture(bitmap).ShouldNotBeNull();
					texture.ShouldNotBe(previous ?? default, $"{backend.Name}: frame {frame}'s texels should be a new upload");
					previous = texture;
					painter.ResidentSourceCount.ShouldBe(2, $"{backend.Name}: white and the bitmap, the replaced texture released");

					var (w, _, rgba) = PngReader.Read(path);
					var i = (16 * w + 16) * 4;
					((int)rgba[i], (int)rgba[i + 1], (int)rgba[i + 2]).ShouldBe(((int)r, (int)g, (int)b), $"{backend.Name}: frame {frame}");
				}
				finally
				{
					if (File.Exists(path)) File.Delete(path);
				}
			}
		}
	}

	/// <summary>
	/// A bitmap rewritten every frame costs the managed heap only the copy of its texels that each replacement uploads.
	/// SUIsei's invalidate and composite allocate nothing, and the engine's frame allocates nothing; <see cref="UIPainter.Prepare"/>
	/// replaces the texture, and the upload copies the texels into a new <see cref="TextureData"/> each time (plus a few
	/// hundred bytes of the engine's per-texture bookkeeping). Updating a texture in place is the labelled gap that would
	/// take that to zero; until then this pins everything else at zero and the replacement at its texels.
	/// </summary>
	[Test]
	public void A_WriteableBitmap_rewritten_every_frame_allocates_only_the_copy_of_its_texels()
	{
		const int WarmupFrames = 20, MeasuredFrames = 30, Side = 64;
		const long TexelBytes = Side * Side * 4, Bookkeeping = 1024;
		foreach (var backend in AvailableBackends())
		{
			using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
			var host = new UIHost();
			var view = host.CreateRoot(UILayer.Default);
			var bitmap = new WriteableBitmap(Side, Side);
			view.Root.Content = TopLeft(new Image { Source = bitmap, Stretch = Stretch.Fill, Width = Side, Height = Side });
			var painter = UIPainter.Create(engine, host);
			engine.SetGraph(RenderGraph.Define(g =>
			{
				var screen = g.Backbuffer;
				g.Pass("ui").Writes(screen).Draw((painter, view.Surface), static (ctx, ui) => ui.painter.Draw(ctx, ui.Surface));
				g.Present(screen);
			}));

			long hostBytes = 0, prepareBytes = 0, renderBytes = 0;
			for (var frame = 0; frame < WarmupFrames + MeasuredFrames; frame++)
			{
				bitmap.PixelBuffer.Span[0] = (byte)frame;
				var t0 = GC.GetAllocatedBytesForCurrentThread();
				bitmap.Invalidate();
				host.Tick(1.0 / 60.0, 0f, new Vector2(W, H));
				host.Render();
				var t1 = GC.GetAllocatedBytesForCurrentThread();
				painter.Prepare();
				var t2 = GC.GetAllocatedBytesForCurrentThread();
				engine.Render(Inputs);
				var t3 = GC.GetAllocatedBytesForCurrentThread();
				if (frame < WarmupFrames)
					continue;
				hostBytes += t1 - t0;
				prepareBytes += t2 - t1;
				renderBytes += t3 - t2;
			}

			hostBytes.ShouldBe(0L, $"{backend.Name}: invalidating and compositing the bitmap allocated {hostBytes} bytes over {MeasuredFrames} frames.");
			renderBytes.ShouldBe(0L, $"{backend.Name}: the frame drawing the bitmap allocated {renderBytes} bytes over {MeasuredFrames} frames.");
			(prepareBytes / MeasuredFrames).ShouldBeLessThanOrEqualTo(TexelBytes + Bookkeeping,
				$"{backend.Name}: replacing the bitmap's texture allocated {prepareBytes / MeasuredFrames} bytes a frame, more than the copy of its {TexelBytes} bytes of texels.");
		}
	}

	// A canvas of images that tell the two alpha modes and both colour spaces apart: a straight sRGB image at 1:1 and
	// magnified (where the premultiply has to follow the filter, or its transparent texels' colour bleeds into the
	// edges), the same texels premultiplied and linear, and the straight one painting an ellipse through a brush (the
	// vector arm).
	private static UIElement ImagePage()
	{
		var straight = BitmapSource.FromRgba8(4, 4, StraightTexels());
		var premultiplied = BitmapSource.FromRgba8(4, 4, PremultipliedTexels(), TextureAlphaMode.Premultiplied, TextureColorSpace.Linear);
		var canvas = new Canvas { Width = W, Height = H };
		canvas.Children.Add(Placed(new Image { Source = straight, Stretch = Stretch.Fill, Width = 4, Height = 4 }, 8, 8));
		canvas.Children.Add(Placed(new Image { Source = straight, Stretch = Stretch.Fill, Width = 96, Height = 96 }, 24, 16));
		canvas.Children.Add(Placed(new Image { Source = premultiplied, Stretch = Stretch.Fill, Width = 96, Height = 96 }, 140, 16));
		canvas.Children.Add(Placed(new Ellipse { Width = 96, Height = 96, Fill = new ImageBrush { ImageSource = straight } }, 256, 16));
		return canvas;
	}

	// Saturated opaque texels beside half-transparent ones and fully transparent texels whose colour is garbage (bright
	// yellow) — a straight image may hold anything there, and a correct premultiply makes it vanish.
	private static byte[] StraightTexels()
	{
		var texels = new byte[4 * 4 * 4];
		for (var y = 0; y < 4; y++)
		{
			for (var x = 0; x < 4; x++)
			{
				var o = (y * 4 + x) * 4;
				(byte, byte, byte, byte) t = ((x + y) % 3) switch
				{
					0 => (230, 30, 40, 255),
					1 => (20, 200, 90, 128),
					_ => (255, 255, 0, 0),
				};
				(texels[o], texels[o + 1], texels[o + 2], texels[o + 3]) = t;
			}
		}
		return texels;
	}

	// Linear, premultiplied texels of the same layout: what an app that already premultiplied supplies.
	private static byte[] PremultipliedTexels()
	{
		var texels = new byte[4 * 4 * 4];
		for (var y = 0; y < 4; y++)
		{
			for (var x = 0; x < 4; x++)
			{
				var o = (y * 4 + x) * 4;
				(byte, byte, byte, byte) t = ((x + y) % 3) switch
				{
					0 => (180, 60, 20, 255),
					1 => (10, 64, 100, 128),
					_ => (0, 0, 0, 0),
				};
				(texels[o], texels[o + 1], texels[o + 2], texels[o + 3]) = t;
			}
		}
		return texels;
	}

	// An 8x8 graph target filled with four quadrants by its own pass, shown by a host image in a SUIsei page drawn by a
	// second pass. `bind` and `declare` leave out the painter binding and the UI pass's .Samples; `colorSpace` is what the
	// host image declares about the Rgba8Srgb target.
	private static void RenderHostImage(IRenderBackend backend, string? outPath, bool bind, bool declare, TextureColorSpace colorSpace)
	{
		using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
		var host = new UIHost();
		var view = host.CreateRoot(UILayer.Default);
		var image = new HostImageSource("map", 8, 8, TextureAlphaMode.Premultiplied, colorSpace);
		view.Root.Content = TopLeft(new Image { Source = image, Stretch = Stretch.Fill, Width = 64, Height = 64 });
		var painter = UIPainter.Create(engine, host);
		var white = engine.CreateTexture(TextureData.FromRgba8(1, 1, [255, 255, 255, 255], TextureFormat.Rgba8Unorm, generateMips: false));

		var quadrants = new OverlayVertex[16];
		var indices = new ushort[24];
		Vector4[] colours = [new(1, 0, 0, 1), new(0, 1, 0, 1), new(0, 0, 1, 1), new(1, 1, 1, 1)];
		for (var q = 0; q < 4; q++)
			Quad(quadrants, indices, q, new Vector2(q % 2 * 4, q / 2 * 4), new Vector2(4), colours[q]);
		LoomDrawCommand[] commands = [new(new LoomRect(0, 0, 8, 8), 0, 0, 24, 0, 0)];
		TextureRef[] textures = [white];

		engine.SetGraph(RenderGraph.Define(g =>
		{
			g.BackbufferClear = Background;
			var map = g.Domain("map", 8, 8).Color("map", Format.Rgba8Srgb);
			if (bind)
				painter.SetExternalTexture("map", map);
			var screen = g.Backbuffer;
			g.Pass("map").Writes(map).Draw(ctx =>
			{
				Span<float> push = [2f / 8, 2f / 8, -1f, -1f, 1f, 0f];
				ctx.DrawList("ui", DrawListIdentity.Transient, textures, quadrants, indices, commands, push);
			});
			var ui = g.Pass("ui");
			if (declare)
				ui.Samples(map);
			ui.Writes(screen).Draw((painter, view.Surface), static (ctx, ui) => ui.painter.Draw(ctx, ui.Surface));
			g.Present(screen);
		}));

		for (var frame = 1; frame <= 3; frame++)
		{
			Frame(host, painter);
			if (frame == 2 && outPath is not null)
				engine.RequestCapture(outPath);
			engine.Render(Inputs);
		}
	}

	private static void Frame(UIHost host, UIPainter painter)
	{
		host.Tick(1.0 / 60.0, 0f, new Vector2(W, H));
		host.Render();
		painter.Prepare();
	}

	private static T Placed<T>(T element, float left, float top) where T : UIElement
	{
		Canvas.SetLeft(element, left);
		Canvas.SetTop(element, top);
		return element;
	}

	// The root lays its content out over the whole surface; this pins an element to the top-left corner at its own size.
	private static T TopLeft<T>(T element) where T : FrameworkElement
	{
		element.HorizontalAlignment = HorizontalAlignment.Left;
		element.VerticalAlignment = VerticalAlignment.Top;
		return element;
	}

	private static void Fill(Span<byte> rgba, byte r, byte g, byte b)
	{
		for (var o = 0; o < rgba.Length; o += 4)
			(rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = (r, g, b, 255);
	}

	private static void Quad(OverlayVertex[] vertices, ushort[] indices, int q, Vector2 origin, Vector2 size, Vector4 colour)
	{
		var c = new Half4((Half)colour.X, (Half)colour.Y, (Half)colour.Z, (Half)colour.W);
		for (var v = 0; v < 4; v++)
		{
			var uv = new Vector2(v is 1 or 2 ? 1f : 0f, v >= 2 ? 1f : 0f);
			vertices[q * 4 + v] = new OverlayVertex(origin + uv * size, uv, c, Vector4.Zero);
		}
		ReadOnlySpan<int> corners = [0, 1, 2, 0, 2, 3];
		for (var i = 0; i < 6; i++)
			indices[q * 6 + i] = (ushort)(q * 4 + corners[i]);
	}

	private static void Expect(List<string> failures, IRenderBackend backend, byte[] rgba, int width, int x, int y, (int R, int G, int B) want)
	{
		var i = (y * width + x) * 4;
		(int R, int G, int B) actual = (rgba[i], rgba[i + 1], rgba[i + 2]);
		if (Math.Abs(actual.R - want.R) > 2 || Math.Abs(actual.G - want.G) > 2 || Math.Abs(actual.B - want.B) > 2)
			failures.Add($"{backend.Name}: ({x},{y}) is {actual}, expected {want}");
	}

	private static List<string> CaptureErrors(out Action<LoomLogLevel, string>? previousSink)
	{
		var errors = new List<string>();
		previousSink = LoomLog.Sink;
		LoomLog.Sink = (level, message) =>
		{
			if (level == LoomLogLevel.Error)
				lock (errors) errors.Add(message);
		};
		return errors;
	}

	private static IReadOnlyList<IRenderBackend> AvailableBackends()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("no GPU backend available on this box.");
		return Backends.Available;
	}

	private static T? Unwrap<T>(Exception? e) where T : Exception
	{
		for (; e is not null; e = e.InnerException)
			if (e is T match)
				return match;
		return null;
	}
}
