namespace Loom.Backend.WebGPU;

/// <summary>
/// A WebGPU backend failure — a validation error the browser reported (drained from the JS shim's error queue),
/// a missing shader artifact, or a protocol mismatch between the C# command writer and the JS replayer.
/// </summary>
public sealed class WebGpuException(string message) : Exception(message);
