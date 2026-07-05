using System.Numerics;
using System.Runtime.Versioning;

using Loom.Core.Animation;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Backend.WebGPU;

/// <summary>
/// The per-frame <see cref="IRenderContext"/> the WebGPU backend hands each pass closure. It records the
/// closure's draw/dispatch verbs into the frame's <see cref="WebGpuCommandWriter"/> (appended after the pass's
/// BeginRenderPass, before its EndPass) and stages their data into the <see cref="WebGpuUploadArena"/>. Pure:
/// closures pull frame inputs from here, never capture.
/// <para>
/// The draw verbs are filled in as the backend grows (fullscreen techniques, meshes, compute, draw lists);
/// until then they throw so an unported code path fails loudly rather than silently dropping work.
/// </para>
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class WebGpuRenderContext(WebGpuEngine engine) : IRenderContext
{
	private readonly WebGpuEngine _engine = engine;

	public float DeltaTime { get; private set; }
	public float Time { get; private set; }
	public bool HdrOutput => false; // browser output is the SDR canvas; HDR canvas negotiation is a later concern

	public IReadOnlyList<(DrawListId Id, DrawListData Data)> DrawLists { get; private set; } = [];

	/// <summary>Set the per-frame inputs before the graph records.</summary>
	public void BeginFrame(float deltaTime, float time, IReadOnlyList<(DrawListId Id, DrawListData Data)> drawLists)
	{
		DeltaTime = deltaTime;
		Time = time;
		DrawLists = drawLists;
	}

	public void FullScreen(string technique, params ReadOnlySpan<ResourceHandle> inputs) =>
		throw new WebGpuException($"FullScreen('{technique}') is not yet implemented on the WebGPU backend.");

	public void FullScreen(string technique, ReadOnlySpan<float> push, params ReadOnlySpan<ResourceHandle> inputs) =>
		throw new WebGpuException($"FullScreen('{technique}') is not yet implemented on the WebGPU backend.");

	public void Dispatch(string technique, (uint X, uint Y, uint Z) groups, ReadOnlySpan<float> push, ReadOnlySpan<ResourceHandle> sampled, ReadOnlySpan<ResourceHandle> storage) =>
		throw new WebGpuException($"Dispatch('{technique}') is not yet implemented on the WebGPU backend.");

	public void Draw(in DrawCall call) =>
		throw new WebGpuException($"Draw('{call.Technique}') is not yet implemented on the WebGPU backend.");

	public void DispatchSkin(string technique, Mesh skinnedMesh, ReadOnlySpan<Matrix4x4> palette, BufferHandle output, int vertexCount) =>
		throw new WebGpuException($"DispatchSkin('{technique}') is not yet implemented on the WebGPU backend.");

	public void DrawList<TVertex>(string technique, ReadOnlySpan<Texture> textures, ReadOnlySpan<TVertex> vertices, ReadOnlySpan<ushort> indices, ReadOnlySpan<DrawCommand> commands, ReadOnlySpan<float> push) where TVertex : unmanaged =>
		throw new WebGpuException($"DrawList('{technique}') is not yet implemented on the WebGPU backend.");

	public Texture? ResolveInspectable(int resourceId) => null;
}
