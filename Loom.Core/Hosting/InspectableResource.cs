using Loom.Core.Graph;

namespace Loom.Core.Hosting;

/// <summary>
/// A graph image resource the debug viewer can preview. Backend-neutral — no GPU type; the host enumerates
/// these from <see cref="IEngine.InspectableResources"/> and binds one into a draw list via
/// <see cref="IRenderContext.ResolveInspectable"/>.
/// </summary>
/// <param name="Id">The resource's stable graph id (<see cref="ResourceHandle.Id"/>).</param>
/// <param name="Name">The resource's name.</param>
/// <param name="Kind">Colour or depth (buffers/backbuffer/persistent are not previewed).</param>
/// <param name="Format">The abstract format — lets the host pick a sensible display (grayscale for depth/R, etc.).</param>
public readonly record struct InspectableResource(int Id, string Name, ResourceKind Kind, Format Format)
{
	/// <summary>
	/// Offset added to a graph resource's <see cref="Id"/> to encode it as a draw-list texture id: a texture id at
	/// or above this base refers to a live transient graph image (the resource viewer) instead of a loaded
	/// texture-store entry, so a debug overlay can preview graph contents. Single-sourced here so the backends
	/// agree on the encoding.
	/// </summary>
	public const int DebugTextureBase = 0x10000;
}
