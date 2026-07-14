// Boot the .NET WASM runtime and run the C# entry point. The WebGPU shim is imported here (so the static-web-
// asset import map resolves its fingerprinted URL) and registered under the module name the backend's
// [JSImport]s reference; the device bring-up and render loop are driven from C# (Program.Web.cs).

import { dotnet } from './_framework/dotnet.js'
// loom-webgpu.js ships with the WebGPU backend package as a static web asset — consumed from its RCL path,
// not carried by this app.
import * as loomWebgpu from './_content/Loom.Backend.WebGPU/loom-webgpu.js'

const { setModuleImports, runMain } = await dotnet.create();

setModuleImports('loom-webgpu', { ...loomWebgpu });

await runMain();
