using System.Numerics;
using System.Runtime.InteropServices;

namespace Loom.UI.ImGui;

/// <summary>
/// One Dear ImGui vertex, laid out byte-for-byte like ImGui's native <c>ImDrawVert</c> — pixel-space position, uv,
/// and a packed RGBA8 colour (20 bytes). The <c>imgui</c> technique's vertex layout mirrors this, so each command
/// list's vertex buffer copies straight in as raw bytes (no per-vertex conversion). The colour stays packed; the
/// input assembler unpacks it to a float4 via the <c>UNorm4</c> attribute format.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct ImGuiVertex(Vector2 Position, Vector2 Uv, uint Color);
