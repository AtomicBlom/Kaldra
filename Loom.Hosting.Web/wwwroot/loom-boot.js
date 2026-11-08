// Reusable Loom browser boot helper, shipped as a static web asset (served at
// _content/Loom.Hosting.Web/loom-boot.js). Both the default shell's main.js and an embedder's own bootstrap
// call runLoom({ canvas }) — the canvas selector the *page* owns. It loads the .NET runtime, wires the WebGPU
// backend's JS shim, runs the app's Main (which registers the LoomApplication), hands the canvas to the host's
// StartApp export so the device comes up on that specific canvas, then drives the host's Frame export once an
// animation frame.
//
// Every call into C# here is async, as a multithreaded runtime (WasmEnableThreads) requires: its C# runs on a worker,
// the "deputy", which the page's thread may not call synchronously. A frame still running when the next animation
// frame comes (C# behind the display) is let finish rather than queued behind.
//
// Relative imports resolve from this file's served location under _content/Loom.Hosting.Web/:
//   ../../_framework/dotnet.js          -> /_framework/dotnet.js         (the runtime, at the app root)
//   ../Loom.Backend.WebGPU/loom-webgpu.js -> /_content/Loom.Backend.WebGPU/loom-webgpu.js (sibling package asset)
import * as loomWebgpu from '../Loom.Backend.WebGPU/loom-webgpu.js'

// captureKeys: false leaves every key to the browser, F1, Tab and a lone Alt included (see setBrowserKeyCapture in
// loom-webgpu.js); by default they are kept from it while the player is in the canvas.
export async function runLoom({ canvas = '#loom-canvas', captureKeys = true } = {}) {
	loomWebgpu.setBrowserKeyCapture(captureKeys);
	// A start that fails is put on the page, not only in the console, or "loading .NET runtime" stays up for good. The
	// runtime is imported here rather than at the top for the same reason: a multithreaded runtime on a page without
	// cross-origin isolation asserts as its module loads, which would fail this module before it could say so. Other
	// failures come as an error nothing awaits rather than a rejection here.
	const show = e => loomWebgpu.setStatus(`ERROR: ${e?.message ?? e}`);
	const onError = e => show(e.error ?? e.message);
	const onRejection = e => show(e.reason);
	window.addEventListener('error', onError);
	window.addEventListener('unhandledrejection', onRejection);
	try {
		await start(canvas);
	} catch (e) {
		show(e);
		throw e;
	} finally {
		window.removeEventListener('error', onError);
		window.removeEventListener('unhandledrejection', onRejection);
	}
}

async function start(canvas) {
	const { dotnet } = await import('../../_framework/dotnet.js');
	// A multithreaded runtime runs each thread on a Web Worker from a pool made before the app starts. One started past the
	// pool needs a new worker made while the app runs, and on .NET 11 that hangs the deputy: its frame finishes, but the
	// page never hears so and asks for no more. A pool big enough for an app's threads never needs one. The single-threaded
	// runtime ignores these.
	const { setModuleImports, runMain, getAssemblyExports } = await dotnet
		.withConfig({ pthreadPoolInitialSize: 16, pthreadPoolUnusedSize: 4 })
		.create();
	setModuleImports('loom-webgpu', { ...loomWebgpu });
	// runMain runs the app's Main -> LoomApplication.Run, which (via the web host) registers the app and returns.
	await runMain();
	// The canvas identity comes from the page, not from C#: hand it to the host's [JSExport] to start everything.
	const exports = await getAssemblyExports('Loom.Hosting.Web');
	const host = exports.Loom.Hosting.Web.WebAppHost;
	await host.StartApp(canvas);
	let busy = false;
	const tick = timestamp => {
		if (!busy) {
			busy = true;
			host.Frame(timestamp).catch(e => console.error(e)).finally(() => { busy = false; });
		}
		requestAnimationFrame(tick);
	};
	requestAnimationFrame(tick);
}
