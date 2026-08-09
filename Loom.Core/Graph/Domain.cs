using System.Numerics;

namespace Loom.Core.Graph;

/// <summary>
/// A resolution domain: everything declared in it renders at <see cref="Width"/>×<see cref="Height"/>.
/// This is the mechanism behind pixelation — render the whole world into a small
/// domain, then cross to the backbuffer with an explicit nearest-neighbour upscale.
/// <para>
/// A <c>Domain</c> is an immutable façade over the owning <see cref="GraphBuilder"/>: its
/// <see cref="Color(string,Format,Vector4?,bool)"/>/<see cref="Depth"/> verbs declare targets <em>into that builder</em>. Obtain one
/// from <see cref="Domain"/> or <see cref="GraphBuilder.Backbuffer"/>.
/// </para>
/// </summary>
public sealed class Domain
{
	private readonly GraphBuilder _graph;

	/// <summary>The domain's name (used in dumps and the graph hash).</summary>
	public string Name { get; }

	/// <summary>Width in pixels. For the backbuffer this is the native swapchain width at bake time.</summary>
	public int Width { get; }

	/// <summary>Height in pixels.</summary>
	public int Height { get; }

	/// <summary>True for the swapchain domain (native resolution, holds the present target).</summary>
	public bool IsBackbuffer { get; }

	/// <summary>
	/// For a <em>backbuffer-relative</em> domain (<see cref="GraphBuilder.Domain(string,float,int)"/>), the fraction
	/// of the native backbuffer this domain tracks — its pixel size is resolved against the swapchain at bake and
	/// re-resolved on every resize. 0 for a fixed-size domain (use <see cref="Width"/>/<see cref="Height"/>) or
	/// the native backbuffer domain (<see cref="IsBackbuffer"/>). A relative domain leaves Width/Height at 0; the
	/// backend computes its extent.
	/// </summary>
	public float RelativeScale { get; }

	/// <summary>
	/// MSAA sample count for targets in this domain (1 = no multisampling; otherwise a power of two). The
	/// backend creates this domain's colour/depth targets multisampled at this count. A multisampled target
	/// must be <em>resolved</em> to a single-sample target before it can be sampled or presented.
	/// </summary>
	public int Samples { get; }

	internal Domain(GraphBuilder graph, string name, int width, int height, bool isBackbuffer, float relativeScale = 0f, int samples = 1)
	{
		_graph = graph;
		Name = name;
		Width = width;
		Height = height;
		IsBackbuffer = isBackbuffer;
		RelativeScale = relativeScale;
		Samples = samples;
	}

	/// <summary>
	/// Declare a colour target in this domain. By default the engine clears it to <see cref="Vector4.Zero"/>
	/// (transparent black) on first write — the safe choice for g-buffer targets and UI textures. Pass
	/// <paramref name="clearValue"/> to use a specific colour instead. Set <paramref name="noClear"/> to skip
	/// the clear entirely (<c>LoadOp.DontCare</c>) — only correct when the first pass covers every pixel
	/// (fullscreen passes, bloom pyramid, SSAO output, etc.).
	/// </summary>
	public ColorHandle Color(string name, Format format, Vector4? clearValue = null, bool noClear = false) =>
		(ColorHandle)_graph.AddResource(name, format, ResourceKind.Color, this, scale: 1f, clearValue: clearValue, noClear: noClear);

	/// <summary>
	/// Declare a colour target at a fraction of the domain's resolution (used for mip chains —
	/// e.g. bloom's pyramid). <paramref name="scale"/> multiplies both width and height.
	/// </summary>
	public ColorHandle Color(string name, Format format, float scale, Vector4? clearValue = null, bool noClear = false) =>
		(ColorHandle)_graph.AddResource(name, format, ResourceKind.Color, this, scale, clearValue: clearValue, noClear: noClear);

	/// <summary>
	/// Declare a depth target in this domain. By default the engine clears it to 1 (the far plane) on first write.
	/// <para>
	/// Pass <paramref name="clearDepth"/> <c>0f</c> for <b>reversed-Z</b> — the far plane at 0 — which spends a float
	/// depth buffer's precision where a perspective divide needs it. It only works as a set: the projection must be
	/// reversed too (<c>Camera.Perspective(…, reversed: true)</c>) and every technique drawing into this target must
	/// test <see cref="DepthCompare.GreaterOrEqual"/>. Set <paramref name="noClear"/> to skip the clear entirely
	/// (<c>LoadOp.DontCare</c>), which is only correct when the first pass to write depth covers every pixel.
	/// </para>
	/// </summary>
	public DepthHandle Depth(string name, Format format, float clearDepth = 1f, bool noClear = false) =>
		(DepthHandle)_graph.AddResource(name, format, ResourceKind.Depth, this, scale: 1f, clearDepth: clearDepth, noClear: noClear);

	/// <summary>
	/// Declare a <see cref="ResourceKind.Persistent"/> target sized to this domain — contents survive across
	/// frames and its memory is never aliased. Use a tiny domain (e.g. 1×1) for a scalar
	/// history like the auto-exposure value. (<see cref="GraphBuilder.Persistent"/> is the screen-sized
	/// form anchored to the backbuffer domain.)
	/// <para>
	/// On a <b>fixed-size</b> domain the contents also survive a window resize, since nothing about the image needs to
	/// change. A domain that follows the swapchain (the backbuffer domain, or one declared with a relative scale) makes
	/// this a screen-sized target instead, which a resize reallocates at the new extent — see
	/// <see cref="GraphBuilder.Persistent"/> for what that costs.
	/// </para>
	/// </summary>
	public ColorHandle Persistent(string name, Format format) =>
		(ColorHandle)_graph.AddResource(name, format, ResourceKind.Persistent, this, scale: 1f);

	/// <summary>The pixel dimensions of a target at the given scale within this domain.</summary>
	public (int Width, int Height) Resolve(float scale) =>
		((int)MathF.Round(Width * scale), (int)MathF.Round(Height * scale));
}
