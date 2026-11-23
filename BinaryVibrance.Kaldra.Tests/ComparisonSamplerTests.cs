using BinaryVibrance.Kaldra.Backend.DirectX12;
using BinaryVibrance.Kaldra.Backend.Vulkan;
using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;
using BinaryVibrance.Kaldra.Graph;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// A texture the shader reads by comparison (a shadow sampler) compares by its technique's <c>SampleCompare</c>, on every
/// backend. A depth target holds a known ramp (<c>depth_ramp</c>: 0 at the left edge, 1 at the right) and
/// <c>compare_probe</c> compares it with 0.5, so the function decides which half of the frame passes. Two techniques over
/// the one shader, with opposite functions, draw in the same frame: on Vulkan they share a pipeline layout, so a binding
/// cache keyed without the compare sampler would hand the second the first's. GPU-gated — SKIPPED when no backend is
/// available.
/// </summary>
[NotInParallel] // one GPU engine at a time
public class ComparisonSamplerTests
{
	private const int W = 64, H = 32;

	[Test]
	public void A_compared_input_compares_by_its_techniques_function()
	{
		foreach (var backend in AvailableBackends())
		{
			using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
			engine.DefineTechnique("depth.ramp", new TechniqueDescriptor("depth_ramp", DepthTest: DepthCompare.Always));
			engine.DefineTechnique("compare.le", new TechniqueDescriptor("compare_probe", SampleCompare: DepthCompare.LessOrEqual));
			engine.DefineTechnique("compare.ge", new TechniqueDescriptor("compare_probe", SampleCompare: DepthCompare.GreaterOrEqual));

			ResourceHandle? lessOrEqual = null, greaterOrEqual = null;
			engine.SetGraph(RenderGraph.Define(g =>
			{
				var depth = g.Screen.Depth("ramp", Format.Depth32F);
				var le = g.Screen.Color("le", Format.Rgba8Unorm);
				var ge = g.Screen.Color("ge", Format.Rgba8Unorm);
				g.Pass("ramp").WritesDepth(depth).Draw(ctx => ctx.FullScreen("depth.ramp"));
				g.Pass("le").Samples(depth).Writes(le).Draw(ctx => ctx.FullScreen("compare.le", depth));
				g.Pass("ge").Samples(depth).Writes(ge).Draw(ctx => ctx.FullScreen("compare.ge", depth));
				lessOrEqual = g.Readback(le);
				greaterOrEqual = g.Readback(ge);
				g.Pass("view").Writes(g.Backbuffer).Draw(_ => { });
				g.Present(g.Backbuffer);
			}));

			var inputs = new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 };
			for (var frame = 0; frame < 6; frame++) // a readback is a frame or two old by design
				engine.Render(inputs);

			// 0.5 <= stored passes where the ramp is past halfway, the right; 0.5 >= stored the left
			AssertHalves(engine.Read(lessOrEqual!), leftPasses: false, $"{backend.Name} LessOrEqual");
			AssertHalves(engine.Read(greaterOrEqual!), leftPasses: true, $"{backend.Name} GreaterOrEqual");
		}
	}

	[Test]
	public void A_material_cannot_be_built_for_a_technique_that_compares()
	{
		foreach (var backend in AvailableBackends())
		{
			using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
			engine.DefineTechnique("compare.le", new TechniqueDescriptor("compare_probe", SampleCompare: DepthCompare.LessOrEqual));
			var texture = engine.CreateTexture(TextureData.FromRgba8(1, 1, [255, 255, 255, 255], TextureFormat.Rgba8Unorm, generateMips: false));

			Should.Throw<TechniqueMismatchException>(() => engine.CreateMaterial("compare.le", texture))
				.Message.ShouldContain("by comparison", customMessage: backend.Name);
		}
	}

	// Every texel of the passing half white and of the failing half black, the column either side of the middle left out
	// for the comparison's 2×2 filter. Both halves are checked, so a frame all one colour can't pass.
	private static void AssertHalves(ReadOnlySpan<byte> rgba, bool leftPasses, string what)
	{
		rgba.Length.ShouldBe(W * H * 4, $"{what}: expected one RGBA8 texel per pixel.");
		int wrong = 0, checkedTexels = 0;
		for (var y = 0; y < H; y++)
		{
			for (var x = 0; x < W; x++)
			{
				if (Math.Abs(x - W / 2) <= 1)
					continue;
				var passes = x < W / 2 == leftPasses;
				var red = rgba[(y * W + x) * 4];
				if (passes ? red < 250 : red > 5)
					wrong++;
				checkedTexels++;
			}
		}
		wrong.ShouldBe(0, $"{what}: {wrong} of {checkedTexels} texels compared the wrong way (the {(leftPasses ? "left" : "right")} half should pass).");
	}

	private static List<IRenderBackend> AvailableBackends()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");
		return [.. Backends.Available];
	}
}
