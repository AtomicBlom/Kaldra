using System.Runtime.Versioning;

// This entire backend issues WebGPU work through the browser's JS interop, so it only functions under the
// .NET WebAssembly runtime. Marking the assembly browser-supported lets the CA1416 analyzer verify every
// interop call is reachable only where WebGPU exists, without annotating each helper type individually.
[assembly: SupportedOSPlatform("browser")]
