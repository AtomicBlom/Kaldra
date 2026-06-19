using System.Collections.Immutable;

namespace Loom.Core;

/// <summary>
/// A backend-neutral description of one vertex stream: an ordered attribute set, the byte offset of each
/// attribute (tight-packed in declaration order), the resulting stride, and whether the input assembler
/// advances <see cref="VertexInputRate.PerVertex"/> or <see cref="VertexInputRate.PerInstance"/> (and which
/// binding slot it occupies). This is M12's generalization of the old single hard-coded
/// <c>Position/Normal/Uv</c> layout: a <see cref="Mesh"/> is uploaded against a layout, a technique declares
/// the layout(s) it consumes, and the backend builds its pipeline vertex-input + bind stride from them. The
/// layout is the <em>shared contract</em> between a mesh and the technique that draws it — a stride mismatch is
/// a clear engine error, not a silent corruption.
/// <para>
/// Backend-neutral by construction (no graphics-API type), so the same layout drives Vulkan and DX12. It is the
/// natural output of an asset importer too — a glTF loader reads accessors and emits a layout + packed buffer
/// (PlanningPhase2 §6: assets are loader-owned, Core never gains a decode dependency).
/// </para>
/// </summary>
public sealed class VertexLayout : IEquatable<VertexLayout>
{
	/// <summary>The attributes, in declaration (and offset) order.</summary>
	public ImmutableArray<VertexAttribute> Attributes { get; }

	/// <summary>The byte offset of each attribute within one element — tight-packed in declaration order.</summary>
	public ImmutableArray<int> Offsets { get; }

	/// <summary>The size of one element in bytes (the buffer's per-element stride).</summary>
	public int Stride { get; }

	/// <summary>Whether this stream advances per vertex or per instance.</summary>
	public VertexInputRate InputRate { get; }

	/// <summary>The binding / input slot this stream occupies (0 for the per-vertex mesh stream, 1 for the per-instance stream).</summary>
	public int Binding { get; }

	public VertexLayout(params ReadOnlySpan<VertexAttribute> attributes)
		: this(VertexInputRate.PerVertex, binding: 0, attributes) { }

	public VertexLayout(VertexInputRate inputRate, int binding, params ReadOnlySpan<VertexAttribute> attributes)
	{
		if (attributes.Length == 0)
			throw new ArgumentException("A vertex layout needs at least one attribute.", nameof(attributes));

		var builder = ImmutableArray.CreateBuilder<VertexAttribute>(attributes.Length);
		var offsets = ImmutableArray.CreateBuilder<int>(attributes.Length);
		var offset = 0;
		foreach (var attribute in attributes)
		{
			builder.Add(attribute);
			offsets.Add(offset);
			offset += attribute.Format.ByteSize();
		}
		Attributes = builder.MoveToImmutable();
		Offsets = offsets.MoveToImmutable();
		Stride = offset;
		InputRate = inputRate;
		Binding = binding;
	}

	/// <summary>
	/// Loom's canonical interleaved mesh layout — <c>Position</c> (float3) + <c>Normal</c> (float3) +
	/// <c>Uv</c> (float2) + <c>Tangent</c> (float4), 48 bytes, mirroring <see cref="Vertex"/>. The default for
	/// any mesh technique. The tangent (M11 normal mapping; glTF <c>xyz</c>+handedness convention) sits at
	/// location 3 — shaders that don't read it simply ignore the attribute.
	/// </summary>
	public static VertexLayout MeshStandard { get; } = new(
		new VertexAttribute("POSITION", VertexFormat.Float3),
		new VertexAttribute("NORMAL", VertexFormat.Float3),
		new VertexAttribute("TEXCOORD", VertexFormat.Float2),
		new VertexAttribute("TANGENT", VertexFormat.Float4));

	/// <summary>
	/// The standard per-instance stream for instanced drawing — a 4×4 world matrix as four <c>ITRANSFORM</c>
	/// rows (4 × float4, 64 bytes), advancing one per instance at binding 1. A CPU-uploaded
	/// <see cref="System.Numerics.Matrix4x4"/> per instance lands here directly; the shader rebuilds the matrix
	/// and applies it before the camera's view-projection.
	/// </summary>
	public static VertexLayout Transform { get; } = new(
		VertexInputRate.PerInstance,
		binding: 1,
		new VertexAttribute("ITRANSFORM", VertexFormat.Float4),
		new VertexAttribute("ITRANSFORM", VertexFormat.Float4),
		new VertexAttribute("ITRANSFORM", VertexFormat.Float4),
		new VertexAttribute("ITRANSFORM", VertexFormat.Float4));

	public bool Equals(VertexLayout? other)
	{
		if (other is null)
			return false;
		if (ReferenceEquals(this, other))
			return true;
		return InputRate == other.InputRate
			&& Binding == other.Binding
			&& Attributes.AsSpan().SequenceEqual(other.Attributes.AsSpan());
	}

	public override bool Equals(object? obj) => obj is VertexLayout other && Equals(other);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(InputRate);
		hash.Add(Binding);
		foreach (var attribute in Attributes)
			hash.Add(attribute);
		return hash.ToHashCode();
	}
}
