using System.Text.Json;

namespace Loom.Backend.WebGPU;

/// <summary>
/// The "surface" the host hands <see cref="WebGpuBackend.Create"/> — the browser has no native window handle,
/// so this carries the canvas selector plus the capabilities the JavaScript side reported when the device was
/// requested (<c>initDevice</c> must complete before the engine is created; WebGPU device acquisition is a
/// promise, and everything after it is synchronous).
/// </summary>
public sealed record WebGpuSurface
{
	/// <summary>CSS selector of the target canvas (e.g. <c>"#loom-canvas"</c>).</summary>
	public required string CanvasSelector { get; init; }

	/// <summary>Human-readable adapter description reported by the browser.</summary>
	public required string AdapterName { get; init; }

	/// <summary>The canvas's preferred texture format name (e.g. <c>"bgra8unorm"</c>).</summary>
	public required string PreferredFormat { get; init; }

	/// <summary>Required alignment for dynamic uniform-buffer offsets (the push-constant ring slice stride).</summary>
	public required int MinUniformBufferOffsetAlignment { get; init; }

	/// <summary>Whether the device granted <c>float32-filterable</c> (linear sampling of r32/rg32/rgba32 float).</summary>
	public required bool Float32Filterable { get; init; }

	/// <summary>Parse the JSON capability blob returned by the JS shim's <c>initDevice</c>.</summary>
	public static WebGpuSurface FromCapsJson(string canvasSelector, string capsJson)
	{
		using var doc = JsonDocument.Parse(capsJson);
		var root = doc.RootElement;
		return new WebGpuSurface
		{
			CanvasSelector = canvasSelector,
			AdapterName = root.TryGetProperty("adapter", out var a) ? a.GetString() ?? "WebGPU adapter" : "WebGPU adapter",
			PreferredFormat = root.GetProperty("format").GetString() ?? "bgra8unorm",
			MinUniformBufferOffsetAlignment = root.TryGetProperty("minUniformBufferOffsetAlignment", out var u) ? u.GetInt32() : 256,
			Float32Filterable = root.TryGetProperty("float32Filterable", out var f) && f.GetBoolean(),
		};
	}
}
