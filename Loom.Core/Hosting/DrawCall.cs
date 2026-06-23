using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Materials;

namespace Loom.Core.Hosting;

/// <summary>
/// A single raster draw command for <see cref="IRenderContext.Draw"/>. Collapses the per-axis overloads into
/// one descriptor: geometry source (mesh / skinned / procedural), instancing (none / CPU / GPU), material,
/// view, and extra inputs (uniforms or sampled). All fields default to their zero/null values.
/// </summary>
/// <remarks>
/// A <c>ref struct</c> so CPU instance spans are held without heap allocation. Construct via
/// <see cref="Create"/> for the common mesh+technique case, or with an object initializer for richer calls:
/// <code>
/// ctx.Draw(DrawCall.Create(cube, "gbuffer"));
/// ctx.Draw(new DrawCall { Mesh = quad, Technique = "light", View = ctx.Views[ViewKeys.Sun] });
/// ctx.Draw(new DrawCall { Mesh = quad, Technique = "sprite", Instances = DrawCall.From(sprites) });
/// </code>
/// </remarks>
public ref struct DrawCall
{
	/// <summary>The technique (registered via <c>IEngine.DefineTechnique</c>) that selects the pipeline.</summary>
	public required string Technique;

	/// <summary>
	/// The mesh to draw. <c>null</c> for procedural draws where the vertex shader fabricates geometry from
	/// <c>SV_VertexID</c> (e.g. the GPU-particle path); set <see cref="VertexCount"/> for those.
	/// </summary>
	public Mesh? Mesh;

	/// <summary>
	/// When set, overrides the vertex source with this pre-skinned buffer while the mesh supplies its index
	/// buffer (the compute pre-skin path). The buffer must be declared with <c>.ReadsVertex</c>.
	/// </summary>
	public BufferHandle? SkinnedVertices;

	/// <summary>
	/// Vertices per instance fabricated from <c>SV_VertexID</c> when <see cref="Mesh"/> is <c>null</c>
	/// (procedural instanced draws). Ignored for mesh-based draws.
	/// </summary>
	public int VertexCount;

	/// <summary>
	/// Per-instance data. Default (empty) = single draw. Set via <see cref="From{T}"/> for CPU-driven
	/// instances or <see cref="Gpu"/> for a GPU-resident buffer.
	/// </summary>
	public InstanceSource Instances;

	/// <summary>
	/// Material whose textures fill the technique's reflected sampler slots in order. <c>null</c> for
	/// untextured techniques (depth-only, shadow, wire overlay).
	/// </summary>
	public Material? Material;

	/// <summary>
	/// The view to render from. <c>null</c> uses the frame's default view (<see cref="IRenderContext.View"/>).
	/// Shadow passes supply <c>ctx.Views[ViewKeys.Sun]</c>; cascade passes supply <c>ctx.Views[ViewKeys.Cascade(i)]</c>.
	/// </summary>
	public Camera? View;

	/// <summary>
	/// Per-frame scalar uniforms staged into the technique's constant buffer (the uniform seam). The technique
	/// must declare a uniform buffer; values are read in the vertex stage (e.g. wind direction + strength).
	/// </summary>
	public ReadOnlySpan<float> Uniforms;

	/// <summary>
	/// Graph resources bound as sampled inputs 0..N-1, in the same order as <c>FullScreen</c> supplies them.
	/// Each must be declared via <c>.Samples</c>; the graph derives the read dependency and shader-read transition.
	/// </summary>
	public ReadOnlySpan<ResourceHandle> Sampled;

	/// <summary>Short form for the common single-mesh, no-material, primary-view draw.</summary>
	public static DrawCall Create(Mesh mesh, string technique) => new() { Mesh = mesh, Technique = technique };

	/// <summary>
	/// Type-erase <paramref name="instances"/> to bytes + stride for use as <see cref="Instances"/>. The engine
	/// uploads the span to a per-frame host-visible ring; no heap allocation.
	/// </summary>
	public static InstanceSource From<T>(ReadOnlySpan<T> instances) where T : unmanaged
		=> InstanceSource.Cpu(MemoryMarshal.AsBytes(instances), Unsafe.SizeOf<T>());

	/// <summary>
	/// Build a GPU-resident instance source from <paramref name="buffer"/> (a <c>.ReadsVertex</c> resource a
	/// compute pass filled) and <paramref name="count"/> instances. The engine derives the compute→vertex barrier.
	/// </summary>
	public static InstanceSource Gpu(BufferHandle buffer, int count)
		=> InstanceSource.FromGpu(buffer, count);

	/// <summary>
	/// The per-instance data for a <see cref="DrawCall"/>: CPU-sourced (uploaded per frame) or GPU-resident.
	/// Construct via <see cref="DrawCall.From{T}"/> or <see cref="DrawCall.Gpu"/>; the default is empty (single draw).
	/// </summary>
	public ref struct InstanceSource
	{
		/// <summary>Type-erased CPU instance bytes (empty when GPU-driven).</summary>
		public ReadOnlySpan<byte> CpuData;
		/// <summary>Bytes per instance element (sizeof(T) from <see cref="DrawCall.From{T}"/>).</summary>
		public int Stride;
		/// <summary>GPU-resident instance buffer (null when CPU-driven).</summary>
		public BufferHandle? GpuBuffer;
		private int _gpuCount;

		/// <summary><c>true</c> when no instance data is set — the draw executes once.</summary>
		public bool IsEmpty => CpuData.IsEmpty && GpuBuffer is null;

		/// <summary>Total instance count: CPU data bytes / stride, or the explicit GPU count.</summary>
		public int Count => CpuData.IsEmpty ? _gpuCount : CpuData.Length / Stride;

		internal static InstanceSource Cpu(ReadOnlySpan<byte> data, int stride) => new() { CpuData = data, Stride = stride };
		internal static InstanceSource FromGpu(BufferHandle buffer, int count) => new() { GpuBuffer = buffer, _gpuCount = count };
	}
}
