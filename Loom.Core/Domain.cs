namespace Loom.Core;

/// <summary>
/// A resolution domain: everything declared in it renders at <see cref="Width"/>×<see cref="Height"/>.
/// This is the mechanism behind pixelation (Planning.md §9) — render the whole world into a small
/// domain, then cross to the backbuffer with an explicit nearest-neighbour upscale.
/// <para>
/// A <c>Domain</c> is an immutable façade over the owning <see cref="GraphBuilder"/>: its
/// <see cref="Color"/>/<see cref="Depth"/> verbs declare targets <em>into that builder</em>. Obtain one
/// from <see cref="GraphBuilder.Domain(string,int,int)"/> or <see cref="GraphBuilder.Backbuffer"/>.
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

	internal Domain(GraphBuilder graph, string name, int width, int height, bool isBackbuffer)
	{
		_graph = graph;
		Name = name;
		Width = width;
		Height = height;
		IsBackbuffer = isBackbuffer;
	}

	/// <summary>Declare a colour target in this domain.</summary>
	public ResourceHandle Color(string name, Format format) =>
		_graph.AddResource(name, format, ResourceKind.Color, this, scale: 1f);

	/// <summary>
	/// Declare a colour target at a fraction of the domain's resolution (used for mip chains —
	/// e.g. bloom's pyramid). <paramref name="scale"/> multiplies both width and height.
	/// </summary>
	public ResourceHandle Color(string name, Format format, float scale) =>
		_graph.AddResource(name, format, ResourceKind.Color, this, scale);

	/// <summary>Declare a depth target in this domain.</summary>
	public ResourceHandle Depth(string name, Format format) =>
		_graph.AddResource(name, format, ResourceKind.Depth, this, scale: 1f);

	/// <summary>The pixel dimensions of a target at the given scale within this domain.</summary>
	public (int Width, int Height) Resolve(float scale) =>
		((int)MathF.Round(Width * scale), (int)MathF.Round(Height * scale));
}
