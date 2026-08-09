using System.Numerics;
using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Tests;

/// <summary>
/// The engine's CPU↔GPU data channels, end to end on a real device: <see cref="IEngine.Write(ConstantsHandle, in
/// object)"/>'s validation, growable storage growing under a write that overflows its declared size, and the
/// <c>g.Readback</c> round-trip actually returning the texels the frame produced. These live on
/// <c>EngineBase</c>, shared by every backend, so each assertion runs against every available backend rather than
/// against whichever one happens to be installed. GPU-gated — SKIPPED when no backend is available.
/// </summary>
[NotInParallel] // one GPU engine at a time
public class EngineDataChannelTests
{
	private const int W = 64, H = 48;

	[Test]
	public void Writing_a_handle_the_active_graph_does_not_declare_is_refused()
	{
		foreach (var backend in AvailableBackends())
		{
			ConstantsHandle? orphan = null;
			StorageBufferHandle? orphanStorage = null;
			// A graph the engine never sets: its handles are minted, but they belong to nothing the engine is running.
			RenderGraph.Define(g =>
			{
				g.Screen.Color("pad0", Format.Rgba8Unorm);
				g.Screen.Color("pad1", Format.Rgba8Unorm);
				g.Screen.Color("pad2", Format.Rgba8Unorm);
				orphan = g.Constants<Matrix4x4>("orphan");
				orphanStorage = g.Storage<Vector4>("orphan_lights", 4);
			});

			using var engine = Engine(backend);
			engine.SetGraph(Minimal());

			// Named, and pointing at the fix: the write is silently lost otherwise, and a shader reading zeroes is a
			// long way from the mistake that caused it.
			var constants = Should.Throw<InvalidOperationException>(() => engine.Write(orphan!, Matrix4x4.Identity));
			constants.Message.ShouldContain("orphan");
			constants.Message.ShouldContain("SetGraph");

			Should.Throw<InvalidOperationException>(() => engine.Write<Vector4>(orphanStorage!, [Vector4.Zero]));
		}
	}

	[Test]
	public void Writing_a_differently_sized_value_to_a_constants_channel_is_refused()
	{
		foreach (var backend in AvailableBackends())
		{
			ConstantsHandle? viewproj = null;
			var graph = RenderGraph.Define(g =>
			{
				var screen = g.Backbuffer;
				viewproj = g.Constants<Matrix4x4>("viewproj");
				g.Pass("fill").Writes(screen).Draw(_ => { });
				g.Present(screen);
			});

			using var engine = Engine(backend);
			engine.SetGraph(graph);

			// 64 declared bytes, 16 written: the block would be filled with a quarter of a matrix and three quarters of
			// whatever was there before, which no shader can detect.
			var thrown = Should.Throw<ArgumentException>(() => engine.Write(viewproj!, Vector4.Zero));
			thrown.Message.ShouldContain("64");
			thrown.Message.ShouldContain("16");
		}
	}

	[Test]
	public void A_write_past_a_non_growable_storage_buffer_is_refused_and_a_growable_one_grows()
	{
		foreach (var backend in AvailableBackends())
		{
			StorageBufferHandle? fixedSize = null;
			StorageBufferHandle? growable = null;
			var graph = RenderGraph.Define(g =>
			{
				var screen = g.Backbuffer;
				fixedSize = g.Storage<Vector4>("fixed_lights", 4);
				growable = g.Storage<Vector4>("growable_lights", 4, growable: true);
				g.Pass("fill").Writes(screen).Draw(_ => { });
				g.Present(screen);
			});

			using var engine = Engine(backend);
			engine.SetGraph(graph);

			var tooMany = new Vector4[32];
			var refused = Should.Throw<ArgumentException>(() => engine.Write<Vector4>(fixedSize!, tooMany));
			refused.Message.ShouldContain("growable: true"); // the message has to name the way out, not just the wall

			// The growable one reallocates instead — including the backend's per-slot GPU buffers, behind a GPU drain.
			engine.Write<Vector4>(growable!, tooMany);
			// Growing again from the already-grown size exercises the geometric path a second time, and a write that
			// still fits must not reallocate anything.
			engine.Write<Vector4>(growable!, new Vector4[64]);
			engine.Write<Vector4>(growable!, new Vector4[2]);

			// The engine keeps rendering with the reallocated buffers bound.
			var inputs = new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 };
			for (var frame = 0; frame < 3; frame++)
				engine.Render(inputs);
		}
	}

	[Test]
	public void A_readback_target_round_trips_the_texels_the_frame_produced()
	{
		// The whole GPU→CPU edge in one assertion: the pass writes a known horizontal gradient into an R16F target,
		// g.Readback copies it out after the frame, and IEngine.Read hands back those texels a frame or two later.
		// Nothing samples the target, so its only reason to survive the frame is the readback edge — the shape a
		// picking graph reaches the moment it stops drawing a hover outline.
		foreach (var backend in AvailableBackends())
		{
			ResourceHandle? ids = null;
			var graph = RenderGraph.Define(g =>
			{
				var screen = g.Backbuffer;
				var target = g.Screen.Color("ids", Format.R16F);
				g.Pass("picking").Writes(target).Draw(ctx => ctx.FullScreen("probe.opaque", Ramp));
				g.Pass("view").Writes(screen).Draw(ctx => ctx.FullScreen("probe.opaque", Ramp));
				ids = g.Readback(target);
				g.Present(screen);
			});

			using var engine = Engine(backend);
			engine.SetGraph(graph);

			var inputs = new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 };
			for (var frame = 0; frame < 6; frame++) // readback is a frame or two old by design; 6 clears the latency
				engine.Render(inputs);

			var bytes = engine.Read(ids!);
			bytes.Length.ShouldBe(W * H * sizeof(ushort), $"{backend.Name}: expected one R16F texel per pixel.");

			// probe writes `pushed.rgb * uv.x`, so row 0 ramps 0 → ~2 left to right. Sample the middle row.
			var row = H / 2;
			var left = Texel(bytes, row, 0);
			var right = Texel(bytes, row, W - 1);
			var middle = Texel(bytes, row, W / 2);

			left.ShouldBeLessThan(0.1f, $"{backend.Name}: the left edge should be ~0 (got {left}).");
			right.ShouldBeGreaterThan(1.8f, $"{backend.Name}: the right edge should be ~2 (got {right}).");
			// Not merely non-empty: the gradient's shape has to survive the copy, which a stale or partially
			// discarded tile would not reproduce.
			middle.ShouldBeInRange(0.9f, 1.1f, $"{backend.Name}: the midpoint should be ~1 (got {middle}).");
		}
	}

	private static float Texel(ReadOnlySpan<byte> r16f, int row, int column) =>
		(float)BitConverter.ToHalf(r16f[((row * W + column) * sizeof(ushort))..]);

	private static readonly float[] Ramp = [2f, 2f, 2f, 1f]; // probe scales this by uv.x → a 0→2 horizontal ramp

	private static RenderGraph Minimal() => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		g.Pass("fill").Writes(screen).Draw(_ => { });
		g.Present(screen);
	});

	private static IEngine Engine(IRenderBackend backend)
	{
		var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
		engine.DefineTechnique("probe.opaque", new TechniqueDescriptor("probe", HasFragmentPush: true));
		return engine;
	}

	private static IReadOnlyList<IRenderBackend> AvailableBackends()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");
		return Backends.Available;
	}
}
