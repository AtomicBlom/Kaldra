using System.Numerics;
using System.Runtime.InteropServices;

using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// An app-writable storage buffer (<see cref="GraphBuilder.Storage{T}"/>) read as an SSBO, end to end: the host
/// fills a four-element colour array by handle (<see cref="IEngine.Write{T}(StorageBufferHandle, System.ReadOnlySpan{T})"/>),
/// a fullscreen pass <c>.Reads</c> it, and the shader paints each screen quadrant with a different array element.
/// Distinct colours at the four corners prove the host-written array reached the shader at the right indices — the
/// read-only structured-buffer path (Vulkan storage buffer; DirectX12 root SRV), the array sibling of the parameter buffer.
/// </summary>
internal sealed class StorageBufferDemo : IDemo
{
	[StructLayout(LayoutKind.Sequential)]
	private readonly record struct Color(Vector4 Rgba);

	private IEngine _engine = null!;
	private StorageBufferHandle _palette = null!;

	public string Name => "Storage buffer";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("ssbo.palette", new TechniqueDescriptor("ssbo_palette"));
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		_palette = g.Storage<Color>("ssbo.palette.data", 4);
		g.Pass("ssbo")
			.Reads(_palette)
			.Writes(screen)
			.Draw(ctx => ctx.FullScreen("ssbo.palette"));
		g.Present(screen);
	});

	// Fill the four-element palette each frame (static here; an animated effect would vary it). TL/TR/BL/BR.
	public void Update(Concepts.Camera camera) =>
		_engine.Write<Color>(_palette,
		[
			new Color(new Vector4(0.9f, 0.1f, 0.1f, 1f)), // top-left  → red
			new Color(new Vector4(0.1f, 0.9f, 0.1f, 1f)), // top-right → green
			new Color(new Vector4(0.1f, 0.1f, 0.9f, 1f)), // bottom-left  → blue
			new Color(new Vector4(0.9f, 0.9f, 0.1f, 1f)), // bottom-right → yellow
		]);
}
