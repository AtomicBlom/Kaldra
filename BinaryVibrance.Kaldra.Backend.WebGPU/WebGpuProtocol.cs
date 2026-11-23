namespace BinaryVibrance.Kaldra.Backend.WebGPU;

/// <summary>
/// The version of the C#↔JS command-stream protocol. The host passes it to <c>initDevice</c>, which rejects a
/// shim (<c>kaldra-webgpu.js</c>) built against a different version — so a stale cached script fails at startup
/// rather than mis-replaying commands mid-frame. Bump it whenever the command encoding or interop surface
/// changes incompatibly.
/// </summary>
public static class WebGpuProtocol
{
	public const int Version = 2;
}
