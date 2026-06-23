using Loom.Core.Graph;
using Loom.Core.Hosting;

namespace Loom.Core.Geometry;

/// <summary>
/// An opaque handle to GPU-resident geometry (vertex + index buffers), minted by the engine when the app
/// uploads a mesh and handed back to passes via <see cref="IRenderContext.Draw"/>. Like
/// <see cref="ResourceHandle"/>, it is just an identity the backend resolves to real buffers — the describe
/// layer never sees the GPU resources. Meshes are app-owned, stable resources (the app builds them once and
/// keeps them), so referencing one from a pass's record closure does not break the closure's purity the way
/// capturing per-frame data would ("you hand me draw lists").
/// </summary>
/// <param name="Id">Stable, engine-assigned index the backend resolves to vertex/index buffers.</param>
/// <param name="IndexCount">Number of indices to draw.</param>
public readonly record struct Mesh(int Id, int IndexCount);
