using HelloTriangle;

using Loom.Core.Hosting;

// One line, shared by desktop and web. The build's RID picks the host (Loom.Hosting.Desktop for a native exe,
// Loom.Hosting.Web for browser-wasm); the app itself names no platform.
LoomApplication.Run(new TriangleApp(), args);
