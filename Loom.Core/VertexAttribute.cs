namespace Loom.Core;

/// <summary>
/// One attribute in a <see cref="VertexLayout"/>: a semantic name plus its element format. The semantic is the
/// neutral link between the data and the shader — Vulkan binds by <c>[[vk::location]]</c> (assigned in order),
/// DX12 binds by HLSL semantic name + index (the index derived from how many earlier attributes share the
/// name, so four <c>"ITRANSFORM"</c> rows become <c>ITRANSFORM0..3</c>). Semantic names map 1:1 onto glTF
/// attribute names (POSITION, NORMAL, TEXCOORD, TANGENT, …), which is what makes an importer's output drop
/// straight into a layout (PlanningPhase2 — M15 glTF).
/// </summary>
/// <param name="Semantic">The attribute's semantic name (uppercase by convention — POSITION, NORMAL, TEXCOORD, …).</param>
/// <param name="Format">The attribute's element format.</param>
public readonly record struct VertexAttribute(string Semantic, VertexFormat Format);