using Loom.Core.Graph;

namespace Loom.Core.Hosting;

/// <summary>
/// A graph image resource the debug viewer can preview (M13). Backend-neutral — no GPU type; the host enumerates
/// these from <see cref="IEngine.InspectableResources"/> and asks for a per-frame ImGui texture id via
/// <see cref="IEngine.GetDebugTextureId"/>.
/// </summary>
/// <param name="Id">The resource's stable graph id (<see cref="ResourceHandle.Id"/>).</param>
/// <param name="Name">The resource's name.</param>
/// <param name="Kind">Colour or depth (buffers/backbuffer/persistent are not previewed).</param>
/// <param name="Format">The abstract format — lets the host pick a sensible display (grayscale for depth/R, etc.).</param>
public readonly record struct InspectableResource(int Id, string Name, ResourceKind Kind, Format Format);
