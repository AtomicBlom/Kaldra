using System.Collections.Immutable;
using Loom.Core.Animation;
using Loom.Core.Scene;

namespace Loom.Core.Geometry;

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
/// Backend-neutral by construction (no graphics-API type), so the same layout drives Vulkan and DirectX12. It is the
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
	/// The skinned-mesh per-vertex layout (M15) — <see cref="MeshStandard"/> plus the two skinning attributes:
	/// <c>JOINTS</c> (four bone indices, <see cref="VertexFormat.UInt4"/>) and <c>WEIGHTS</c> (four normalized
	/// blend weights, float4), 80 bytes, mirroring <see cref="SkinnedVertex"/>. A skinned mesh's bind-pose buffer
	/// is uploaded against this; the compute pre-skin pass reads it (+ a bone palette) and writes a plain
	/// <see cref="MeshStandard"/>-shaped buffer, so the depth/g-buffer/shadow techniques draw the skinned output
	/// unchanged. The semantics map 1:1 onto glTF's <c>JOINTS_0</c>/<c>WEIGHTS_0</c>.
	/// </summary>
	public static VertexLayout MeshSkinned { get; } = new(
		new VertexAttribute("POSITION", VertexFormat.Float3),
		new VertexAttribute("NORMAL", VertexFormat.Float3),
		new VertexAttribute("TEXCOORD", VertexFormat.Float2),
		new VertexAttribute("TANGENT", VertexFormat.Float4),
		new VertexAttribute("JOINTS", VertexFormat.UInt4),
		new VertexAttribute("WEIGHTS", VertexFormat.Float4));

	/// <summary>
	/// The 2D overlay per-vertex layout — <c>Position</c> (float2, target pixels) + <c>Uv</c> (float2) +
	/// <c>Color</c> (<see cref="VertexFormat.Half4"/>, premultiplied RGBA16F) + <c>Custom</c> (float4, per-vertex
	/// technique-defined data), 40 bytes, mirroring <see cref="OverlayVertex"/>. The two <c>TEXCOORD</c> attributes
	/// share one repeated semantic (so they bind as <c>TEXCOORD0</c>/<c>TEXCOORD1</c>) exactly like the instanced
	/// layouts' repeated rows — DirectX12 binds by semantic name + index. A draw-list technique declares this layout.
	/// </summary>
	public static VertexLayout Overlay { get; } = new(
		new VertexAttribute("POSITION", VertexFormat.Float2),
		new VertexAttribute("TEXCOORD", VertexFormat.Float2),
		new VertexAttribute("COLOR", VertexFormat.Half4),
		new VertexAttribute("TEXCOORD", VertexFormat.Float4));

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

	/// <summary>
	/// The per-instance stream for 2D sprite/tile batching (M14) — one <see cref="SpriteInstance"/> per quad
	/// (4 × float4, 64 bytes) at binding 1, advancing one per instance. The four rows carry the sprite's
	/// world transform (position + depth + rotation), its world size + rotation pivot, its atlas uv-rect (the
	/// sub-image, which doubles as the sprite-sheet animation frame), and an rgba tint. Paired with a unit-quad
	/// per-vertex stream (<see cref="MeshStandard"/>): the vertex shader expands each corner into the quad the
	/// instance describes and looks the atlas region up from the uv-rect.
	/// <para>
	/// The four rows share one repeated semantic (<c>ISPRITE0..3</c>), exactly like <see cref="Transform"/>'s
	/// <c>ITRANSFORM</c> — DirectX12 binds per-instance attributes by semantic name + index, and four <em>distinct</em>
	/// per-instance semantics bind as zero there (the quads collapse), so an indexed-row layout is required.
	/// </para>
	/// </summary>
	public static VertexLayout Sprite { get; } = new(
		VertexInputRate.PerInstance,
		binding: 1,
		new VertexAttribute("ISPRITE", VertexFormat.Float4), // position.xy, depth, rotation
		new VertexAttribute("ISPRITE", VertexFormat.Float4), // size.xy, pivot.xy
		new VertexAttribute("ISPRITE", VertexFormat.Float4), // atlas uv-rect: u0, v0, u1, v1
		new VertexAttribute("ISPRITE", VertexFormat.Float4)); // rgba tint

	/// <summary>
	/// The per-instance stream for 2D point lights (M14-3) — one <see cref="Light2DInstance"/> per light
	/// (2 × float4, 32 bytes) at binding 1, advancing one per instance. Paired with the same unit-quad
	/// per-vertex stream (<see cref="MeshStandard"/>) the sprites use: the light shader expands the quad to the
	/// light's bounding box, then samples the 2D G-buffer normal per covered pixel and accumulates the light
	/// additively. The two rows carry the light's world position + radius + intensity, and its colour + height
	/// off the plane. As with <see cref="Sprite"/>/<see cref="Transform"/>, the rows share one repeated semantic
	/// (<c>ILIGHT0..1</c>) so DirectX12's semantic-name-plus-index binding picks them up.
	/// </summary>
	public static VertexLayout Light2D { get; } = new(
		VertexInputRate.PerInstance,
		binding: 1,
		new VertexAttribute("ILIGHT", VertexFormat.Float4),  // position.xy, radius, intensity
		new VertexAttribute("ILIGHT", VertexFormat.Float4)); // colour.rgb, height

	/// <summary>
	/// The per-instance stream for the generalized GPU particle system (M14-4) — one particle per instance
	/// (2 × float4, 32 bytes) at <em>binding 0</em>, advancing one per instance. Unlike <see cref="Sprite"/>/
	/// <see cref="Light2D"/>, particles have <em>no</em> paired per-vertex mesh: a compute pass writes this stream
	/// into a storage buffer (`RWStructuredBuffer` of the matching 32-byte struct) and the render shader fabricates
	/// a billboard quad per instance from <c>SV_VertexID</c> — so the stream owns binding 0 outright. This widens
	/// the engine's old hard-coded single-<c>float4</c> particle vertex input into a layout-driven one (a technique
	/// declares it via <c>TechniqueDescriptor.Instance</c> with <c>ParticleInput</c>); a technique that leaves it
	/// null keeps the legacy single-<c>float4</c> path. The two rows carry position + remaining-life and
	/// velocity + per-particle seed; the repeated <c>IPARTICLE0..1</c> semantic matches DirectX12's name-plus-index binding.
	/// </summary>
	public static VertexLayout Particle { get; } = new(
		VertexInputRate.PerInstance,
		binding: 0,
		new VertexAttribute("IPARTICLE", VertexFormat.Float4),  // position.xyz, life (1 at spawn → 0 at death)
		new VertexAttribute("IPARTICLE", VertexFormat.Float4)); // velocity.xyz, seed (per-particle random)

	/// <summary>
	/// The per-instance stream for foliage instancing (M15-5) — one plant per instance (2 × float4, 32 bytes) at
	/// binding 1, paired with a blade/card per-vertex mesh (<see cref="MeshStandard"/>). The two rows carry the
	/// plant's world position + uniform scale, and its yaw + a wind phase + bend stiffness. The instanced foliage
	/// vertex shader rebuilds each plant's transform, sways it by the per-frame wind (height-weighted, offset by
	/// the per-instance phase so the field doesn't move in lockstep), and alpha-tests the blade cutout. As with the
	/// other per-instance layouts, the rows share one repeated semantic (<c>IFOLIAGE0..1</c>) for DirectX12's binding.
	/// </summary>
	public static VertexLayout Foliage { get; } = new(
		VertexInputRate.PerInstance,
		binding: 1,
		new VertexAttribute("IFOLIAGE", VertexFormat.Float4),  // world position.xyz, uniform scale
		new VertexAttribute("IFOLIAGE", VertexFormat.Float4)); // yaw, wind phase, bend stiffness, pad

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
