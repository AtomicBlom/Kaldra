// Reusable Kaldra browser boot helper, shipped as a static web asset (served at
// _content/BinaryVibrance.Kaldra.Hosting.Web/kaldra-boot.js). Both the default shell's main.js and an embedder's own bootstrap
// call runKaldra({ canvas }), the canvas selector the *page* owns, and should: it is the whole contract with the host.
//
// An embedder that boots the runtime itself must do everything runKaldra does, in order:
//   1. Create the .NET runtime with a thread pool big enough for the app (the threads option below).
//   2. setModuleImports('kaldra-webgpu', <the kaldra-webgpu.js module>): the host's and the backend's [JSImport]s.
//   3. Optionally setBrowserKeyCapture(false) on that module (the captureKeys option below).
//   4. runMain(): the app's Main, whose KaldraApplication.Run registers the app and returns.
//   5. Await the host's StartApp(canvasSelector) export (BinaryVibrance.Kaldra.Hosting.Web.WebAppHost): the device comes up on that
//      canvas, and the page starts queueing its input, resizes and pastes for the host.
//   6. Call the host's Frame(timestamp) export once a requestAnimationFrame, for as long as the page runs, never
//      calling it again until the last call's promise has settled. Nothing else runs a frame: StartApp starts no loop
//      of its own, and the input, resizes and pastes queued since the last frame are taken by the next.
//
// Every call into C# here is async, as a multithreaded runtime (WasmEnableThreads) requires: its C# runs on a worker,
// the "deputy", which the page's thread may not call synchronously. A frame still running when the next animation
// frame comes (C# behind the display) is let finish rather than queued behind.
//
// Relative imports resolve from this file's served location under _content/BinaryVibrance.Kaldra.Hosting.Web/:
//   ../../_framework/dotnet.js          -> /_framework/dotnet.js         (the runtime, at the app root)
//   ../BinaryVibrance.Kaldra.Backend.WebGPU/kaldra-webgpu.js -> /_content/BinaryVibrance.Kaldra.Backend.WebGPU/kaldra-webgpu.js (sibling package asset)
import * as kaldraWebgpu from '../BinaryVibrance.Kaldra.Backend.WebGPU/kaldra-webgpu.js'

// canvas: the selector of the page's <canvas> the app draws into.
// threads: the Web Workers a multithreaded runtime makes before the app starts, one a thread. Every thread the app runs
//   at once (its own, the thread pool's, the runtime's) must fit: one started past the pool needs a new worker made while
//   the app runs, and on .NET 11 that hangs the deputy without a word (its frame finishes, but the page never hears so
//   and asks for no more). The single-threaded runtime ignores it.
// captureKeys: false leaves every key to the browser, F1, Tab and a lone Alt included (see setBrowserKeyCapture in
//   kaldra-webgpu.js); by default they are kept from it while the player is in the canvas.
export async function runKaldra({ canvas = '#kaldra-canvas', threads = 16, captureKeys = true } = {}) {
	if (!Number.isInteger(threads) || threads < 1)
		throw new RangeError(`runKaldra: threads must be a whole number of at least 1, not ${threads}`);
	kaldraWebgpu.setBrowserKeyCapture(captureKeys);
	// A start that fails is put on the page, not only in the console, or "loading .NET runtime" stays up for good. The
	// runtime is imported here rather than at the top for the same reason: a multithreaded runtime on a page without
	// cross-origin isolation asserts as its module loads, which would fail this module before it could say so. Other
	// failures come as an error nothing awaits rather than a rejection here.
	const show = e => kaldraWebgpu.setStatus(`ERROR: ${e?.message ?? e}`);
	const onError = e => show(e.error ?? e.message);
	const onRejection = e => show(e.reason);
	window.addEventListener('error', onError);
	window.addEventListener('unhandledrejection', onRejection);
	try {
		await start(canvas, threads);
	} catch (e) {
		show(e);
		throw e;
	} finally {
		window.removeEventListener('error', onError);
		window.removeEventListener('unhandledrejection', onRejection);
	}
}

async function start(canvas, threads) {
	const { dotnet } = await import('../../_framework/dotnet.js');
	const { setModuleImports, runMain, getAssemblyExports } = await dotnet
		.withConfig({ pthreadPoolInitialSize: threads, pthreadPoolUnusedSize: Math.min(4, threads) })
		.create();
	setModuleImports('kaldra-webgpu', { ...kaldraWebgpu });
	// runMain runs the app's Main -> KaldraApplication.Run, which (via the web host) registers the app and returns.
	await runMain();
	// The canvas identity comes from the page, not from C#: hand it to the host's [JSExport] to start everything.
	const exports = await getAssemblyExports('BinaryVibrance.Kaldra.Hosting.Web');
	const host = exports.BinaryVibrance.Kaldra.Hosting.Web.WebAppHost;
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
