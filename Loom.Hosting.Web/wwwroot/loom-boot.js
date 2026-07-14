// Reusable Loom browser boot helper, shipped as a static web asset (served at
// _content/Loom.Hosting.Web/loom-boot.js). Both the default shell's main.js and an embedder's own bootstrap
// call runLoom({ canvas }) — the canvas selector the *page* owns. It loads the .NET runtime, wires the WebGPU
// backend's JS shim, runs the app's Main (which registers the LoomApplication), then hands the canvas to the
// host's StartApp export so device bring-up + the frame loop start on that specific canvas.
//
// Relative imports resolve from this file's served location under _content/Loom.Hosting.Web/:
//   ../../_framework/dotnet.js          -> /_framework/dotnet.js         (the runtime, at the app root)
//   ../Loom.Backend.WebGPU/loom-webgpu.js -> /_content/Loom.Backend.WebGPU/loom-webgpu.js (sibling package asset)
import { dotnet } from '../../_framework/dotnet.js'
import * as loomWebgpu from '../Loom.Backend.WebGPU/loom-webgpu.js'

export async function runLoom({ canvas = '#loom-canvas' } = {}) {
	const { setModuleImports, runMain, getAssemblyExports } = await dotnet.create();
	setModuleImports('loom-webgpu', { ...loomWebgpu });
	// runMain runs the app's Main -> LoomApplication.Run, which (via the web host) registers the app and returns.
	await runMain();
	// The canvas identity comes from the page, not from C#: hand it to the host's [JSExport] to start everything.
	const exports = await getAssemblyExports('Loom.Hosting.Web');
	await exports.Loom.Hosting.Web.WebAppHost.StartApp(canvas);
}
