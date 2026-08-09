using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Materials;

namespace Loom.Core.Hosting;

/// <summary>
/// A single raster draw command for <see cref="IRenderContext.Draw"/>. Collapses the per-axis overloads into
/// one descriptor: geometry source (mesh / skinned / procedural), instancing (none / CPU / GPU), material,
/// and extra inputs (uniforms or sampled). All fields default to their zero/null values.
/// </summary>
/// <remarks>
/// A <c>ref struct</c> so CPU instance spans are held without heap allocation. Construct via
/// <see cref="Create"/> for the common mesh+technique case, or with an object initializer for richer calls:
/// <code>
/// ctx.Draw(DrawCall.Create(cube, "gbuffer"));
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
	/// Which part of <see cref="Mesh"/> to draw. Default (empty) = the whole mesh, which is what nearly every draw
	/// wants. Set it with <see cref="Range"/> to draw one sub-range of a mesh that holds several — a glTF model's
	/// primitives, an atlas of static props — so they can share one vertex and index buffer instead of becoming a
	/// mesh each. Ignored for a procedural draw (no mesh to range into).
	/// </summary>
	public IndexRange Indices;

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
	/// The per-draw uniform block, staged into the technique's per-draw constant buffer (the uniform seam) — raw
	/// bytes matching the shader's cbuffer layout, so any type rides it (a model matrix, a <c>uint</c> object id,
	/// wind parameters), not only floats. Set it from a typed value or span with <see cref="SetUniforms{T}(ref T)"/>
	/// so the caller never marshals bytes by hand. The technique must declare a uniform buffer for this slot; empty
	/// means the draw supplies no per-draw block.
	/// </summary>
	public ReadOnlySpan<byte> Uniforms;

	/// <summary>
	/// Graph resources bound as sampled inputs 0..N-1, in the same order as <c>FullScreen</c> supplies them.
	/// Each must be declared via <c>.Samples</c>; the graph derives the read dependency and shader-read transition.
	/// </summary>
	public ReadOnlySpan<ResourceHandle> Sampled;

	/// <summary>Short form for the common single-mesh, no-material, primary-view draw.</summary>
	public static DrawCall Create(Mesh mesh, string technique) => new() { Mesh = mesh, Technique = technique };

	/// <summary>
	/// Set <see cref="Uniforms"/> from a typed value — the engine marshals it to bytes, so the caller never touches
	/// <see cref="MemoryMarshal"/>. <typeparamref name="T"/> must be laid out to match the shader's per-draw
	/// constant-buffer layout. The bytes <em>alias</em> <paramref name="value"/> rather than copying it, so its storage
	/// must outlive the <see cref="IRenderContext.Draw"/> call that submits this draw (the engine uploads
	/// synchronously). The parameter is <c>ref</c> to enforce that: a property or a temporary has no storage to alias,
	/// and passing one is a compile error instead of a span over a stack slot the JIT is free to reuse. Read, never
	/// written.
	/// </summary>
	public void SetUniforms<T>(ref T value) where T : unmanaged =>
		Uniforms = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1));

	/// <summary>
	/// Set <see cref="Uniforms"/> from a typed span (e.g. an array of per-draw rows or a small scalar block) — the
	/// span counterpart of <see cref="SetUniforms{T}(ref T)"/>. The bytes alias the caller's storage, which must
	/// outlive the <see cref="IRenderContext.Draw"/> call that submits this draw. A span already names storage the
	/// caller owns, so unlike the single-value overload there is nothing for the compiler to reject: passing a span
	/// over a stack buffer that leaves scope before the draw is submitted is on the caller.
	/// </summary>
	public void SetUniforms<T>(ReadOnlySpan<T> values) where T : unmanaged =>
		Uniforms = MemoryMarshal.AsBytes(values);

	/// <summary>
	/// Type-erase a typed span to the byte block for <see cref="Uniforms"/> — the uniform-channel parallel of
	/// <see cref="From{T}"/>, for assigning <see cref="Uniforms"/> directly instead of going through
	/// <see cref="SetUniforms{T}(ReadOnlySpan{T})"/>. The bytes alias the caller's storage (no copy), which must
	/// outlive the <see cref="IRenderContext.Draw"/> call that submits the draw. Lay <typeparamref name="T"/> out to
	/// match the shader's per-draw constant-buffer layout.
	/// </summary>
	public static ReadOnlySpan<byte> Bytes<T>(ReadOnlySpan<T> values) where T : unmanaged => MemoryMarshal.AsBytes(values);

	/// <summary>
	/// Type-erase <paramref name="instances"/> to bytes + stride for use as <see cref="Instances"/>. The engine
	/// uploads the span to a per-frame host-visible ring; no heap allocation.
	/// </summary>
	public static InstanceSource From<T>(ReadOnlySpan<T> instances) where T : unmanaged
		=> InstanceSource.Cpu(MemoryMarshal.AsBytes(instances), Unsafe.SizeOf<T>());

	/// <summary>
	/// Build a GPU-resident instance source from <paramref name="buffer"/> (a <c>.ReadsVertex</c> resource a
	/// compute pass filled) and <paramref name="count"/> instances. The engine derives the compute→vertex barrier.
	/// <para>
	/// When the buffer was declared element-wise (<see cref="GraphBuilder.Buffer{T}"/> /
	/// <see cref="GraphBuilder.Buffer(string,int,Geometry.VertexLayout)"/>), <paramref name="count"/> is checked
	/// against the elements it actually holds. Drawing more instances than that reads off the end of the buffer,
	/// which no backend reports — so it is rejected here, where both numbers are known.
	/// </para>
	/// </summary>
	public static InstanceSource Gpu(BufferHandle buffer, int count)
	{
		ArgumentNullException.ThrowIfNull(buffer);
		ArgumentOutOfRangeException.ThrowIfNegative(count);
		if (buffer.ElementCount > 0 && count > buffer.ElementCount)
		{
			throw new ArgumentOutOfRangeException(nameof(count),
				$"Draw asks for {count} instances from buffer '{buffer.Name}', which was declared to hold {buffer.ElementCount}. " +
				"Size the buffer and the draw from the same count.");
		}
		return InstanceSource.FromGpu(buffer, count);
	}

	/// <summary>
	/// Draw <paramref name="indexCount"/> indices of the mesh starting at <paramref name="firstIndex"/>, with
	/// <paramref name="baseVertex"/> added to every index the range reads. That is the sub-mesh shape every graphics
	/// API already speaks — and the same vocabulary <see cref="DrawCommand"/> uses for a draw list — so one mesh can
	/// hold many primitives and a draw picks one.
	/// </summary>
	public static IndexRange Range(int firstIndex, int indexCount, int baseVertex = 0)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(firstIndex);
		ArgumentOutOfRangeException.ThrowIfNegative(indexCount);
		ArgumentOutOfRangeException.ThrowIfNegative(baseVertex);
		return new IndexRange(firstIndex, indexCount, baseVertex);
	}

	/// <summary>
	/// A sub-range of a mesh's index buffer. The default is empty, which every backend reads as "the whole mesh" —
	/// so a draw that says nothing about ranges behaves exactly as it did before ranges existed. Build one with
	/// <see cref="DrawCall.Range"/>.
	/// </summary>
	public readonly record struct IndexRange(int FirstIndex, int IndexCount, int BaseVertex)
	{
		/// <summary><c>true</c> when no range is set — the draw covers the mesh's whole index buffer.</summary>
		public bool IsEmpty => IndexCount == 0;
	}

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
