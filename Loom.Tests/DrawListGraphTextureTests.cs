using System.Numerics;
using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Diagnostics;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Core.Primitives;
using Loom.UI.Vysara;
using UIHost = Vysara.UIHost;

namespace Loom.Tests;

/// <summary>
/// A draw list samples a graph texture an earlier pass rendered in the same frame, through a <see cref="TextureRef"/>.
/// One pass draws four coloured quadrants into a small target; the next declares it with <c>.Samples</c> and draws a
/// list that shows it beside an engine texture. The quadrants must come back in place — the right image, the right
/// way up — on each desktop backend, with the driver's debug layer silent. The browser's half is the
/// <c>UILiveTexture</c> leg of <see cref="WebGpuParityTests"/>, whose minimap reaches its panel the same way.
/// GPU-gated.
/// </summary>
[NotInParallel] // one GPU engine at a time, and LoomLog.Sink is process-wide
public class DrawListGraphTextureTests
{
	private const int W = 128, H = 64;
	private const int Source = 8;
	private const uint KindFill = 0, KindImage = 4;

	// The quadrants of the source target, top-left, top-right, bottom-left, bottom-right, as 8-bit linear colours. Pure
	// primaries survive the Rgba8Srgb capture exactly.
	private static readonly Vector4[] Quadrants = [new(1, 0, 0, 1), new(0, 1, 0, 1), new(0, 0, 1, 1), new(1, 1, 1, 1)];
	private static readonly Vector4 EngineTint = new(0, 1, 1, 1);

	[Test]
	public void A_draw_list_samples_a_target_rendered_earlier_in_the_frame()
	{
		var failures = new List<string>();
		foreach (var backend in DesktopBackends())
		{
			var path = Path.Combine(Path.GetTempPath(), $"loom-graphtexture-{backend.Name}-{Guid.NewGuid():N}.png");
			var errors = new List<string>();
			var previousSink = LoomLog.Sink;
			LoomLog.Sink = (level, message) =>
			{
				if (level == LoomLogLevel.Error)
					lock (errors) errors.Add(message);
			};
			try
			{
				Render(backend, path, declare: true);
				LoomLog.Sink = previousSink;
				foreach (var error in errors)
					failures.Add($"{backend.Name}: the debug layer reported: {error}");

				var (w, h, rgba) = PngReader.Read(path);
				// The left half shows the source stretched 8x: each quadrant's centre is 16 px in from its corner.
				Expect(backend, failures, rgba, w, 16, 16, Quadrants[0], "the source's top-left quadrant");
				Expect(backend, failures, rgba, w, 48, 16, Quadrants[1], "the source's top-right quadrant");
				Expect(backend, failures, rgba, w, 16, 48, Quadrants[2], "the source's bottom-left quadrant");
				Expect(backend, failures, rgba, w, 48, 48, Quadrants[3], "the source's bottom-right quadrant");
				// The right half samples an engine texture in the same submission, after the graph one.
				Expect(backend, failures, rgba, w, 96, 32, EngineTint, "the engine texture beside it");
				if (h != H)
					failures.Add($"{backend.Name}: captured {w}x{h}, expected {W}x{H}");
			}
			finally
			{
				LoomLog.Sink = previousSink;
				if (File.Exists(path)) File.Delete(path);
			}
		}

		failures.ShouldBeEmpty("A draw list did not show the graph texture it sampled:\n  " + string.Join("\n  ", failures));
	}

	[Test]
	public void A_draw_list_naming_a_target_its_pass_never_declared_fails_the_frame()
	{
		// The record-time half of the rule RecordValidationTests pins device-free: each backend's DrawList hands its
		// textures to the validator, so a list naming an undeclared target stops the frame with the pass and the verb
		// that fixes it, rather than sampling whatever layout the target was left in.
		foreach (var backend in DesktopBackends())
		{
			var thrown = Should.Throw<Exception>(() => Render(backend, outPath: null, declare: false));
			var binding = Unwrap<PassBindingException>(thrown);
			binding.ShouldNotBeNull($"{backend.Name}: expected a PassBindingException, got {thrown}");
			binding.Pass.ShouldBe("list");
			binding.Message.ShouldContain(".Samples(source)");
		}
	}

	private static IReadOnlyList<IRenderBackend> DesktopBackends()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("no GPU backend available on this box.");
		return Backends.Available;
	}

	// Two passes in one frame: "source" fills the four quadrants of an 8x8 target; "list" shows that target over the left
	// half of the screen and an engine texture over the right, as one draw list naming both kinds of texture.
	private static void Render(IRenderBackend backend, string? outPath, bool declare)
	{
		using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
		UIPainter.Create(engine, new UIHost()); // defines the ui technique; this draws no host
		var white = engine.CreateTexture(TextureData.FromRgba8(1, 1, [255, 255, 255, 255], TextureFormat.Rgba8Unorm, generateMips: false));

		var sourceVertices = new OverlayVertex[16];
		var sourceIndices = new ushort[24];
		var half = Source / 2f;
		for (var q = 0; q < 4; q++)
			Quad(sourceVertices, sourceIndices, q, new Vector2(q % 2 * half, q / 2 * half), new Vector2(half), Quadrants[q]);
		DrawCommand[] sourceCommands = [new(new Rect(0, 0, Source, Source), 0, 0, 24, 0, KindFill)];
		TextureRef[] sourceTextures = [white];

		var listVertices = new OverlayVertex[8];
		var listIndices = new ushort[12];
		Quad(listVertices, listIndices, 0, Vector2.Zero, new Vector2(W / 2f, H), Vector4.One);
		Quad(listVertices, listIndices, 1, new Vector2(W / 2f, 0), new Vector2(W / 2f, H), EngineTint);
		DrawCommand[] listCommands =
		[
			new(new Rect(0, 0, W, H), TextureIndex: 1, IndexOffset: 0, IndexCount: 6, VertexOffset: 0, Tag: KindImage),
			new(new Rect(0, 0, W, H), TextureIndex: 0, IndexOffset: 6, IndexCount: 6, VertexOffset: 0, Tag: KindImage),
		];
		var listTextures = new TextureRef[2];
		listTextures[0] = white;

		engine.SetGraph(RenderGraph.Define(g =>
		{
			var source = g.Domain("source", Source, Source).Color("source", Format.Rgba8Unorm);
			listTextures[1] = source;
			var screen = g.Backbuffer;
			g.Pass("source").Writes(source).Draw(ctx =>
			{
				Span<float> push = [2f / Source, 2f / Source, -1f, -1f, 1f, 0f];
				ctx.DrawList("ui", DrawListIdentity.Transient, sourceTextures, sourceVertices, sourceIndices, sourceCommands, push);
			});
			var list = g.Pass("list");
			if (declare)
				list.Samples(source);
			list.Writes(screen).Draw(ctx =>
			{
				Span<float> push = [2f / W, 2f / H, -1f, -1f, 1f, 0f];
				ctx.DrawList("ui", DrawListIdentity.Transient, listTextures, listVertices, listIndices, listCommands, push);
			});
			g.Present(screen);
		}));

		var inputs = new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 };
		for (var frame = 1; frame <= 3; frame++)
		{
			if (frame == 2 && outPath is not null)
				engine.RequestCapture(outPath);
			engine.Render(inputs);
		}
	}

	// Quad q of a list: four vertices at `origin` spanning `size`, uv 0..1 across it, and its two triangles.
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

	private static void Expect(IRenderBackend backend, List<string> failures, byte[] rgba, int width, int x, int y, Vector4 expected, string what)
	{
		var i = (y * width + x) * 4;
		var actual = (rgba[i], rgba[i + 1], rgba[i + 2]);
		var want = ((byte)(expected.X * 255), (byte)(expected.Y * 255), (byte)(expected.Z * 255));
		if (Math.Abs(actual.Item1 - want.Item1) > 2 || Math.Abs(actual.Item2 - want.Item2) > 2 || Math.Abs(actual.Item3 - want.Item3) > 2)
			failures.Add($"{backend.Name}: {what} at ({x},{y}) is {actual}, expected {want}");
	}

	private static T? Unwrap<T>(Exception? e) where T : Exception
	{
		for (; e is not null; e = e.InnerException)
			if (e is T match)
				return match;
		return null;
	}
}
