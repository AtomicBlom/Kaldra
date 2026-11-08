# Loom.Hosting.Web — browser application host

`Loom.Hosting.Web` is the browser (WebAssembly) host for the [Loom render-graph engine](https://github.com/AtomicBlom/Loom). It brings the WebGPU device up on a page-supplied `<canvas>`, drives a `LoomApplication`'s frame loop through `requestAnimationFrame`, and ships a default full-screen HTML shell — so the same `LoomApplication` that runs on the desktop host runs in the browser with no extra code.

## Requirements

- .NET 10 published as WebAssembly (`dotnet publish -r browser-wasm`)
- `Loom.Backend.WebGPU` (referenced transitively) and a browser with WebGPU support

## What's included

- `WebAppHost` — an `IAppHost` with two `[JSExport]`s the JS boot helper calls: `StartApp(canvasSelector)`, which brings the device up on the page's canvas (an embedder owns their own `<canvas>`), and `Frame(timestamp)`, one frame
- `loom-boot.js` — a reusable boot helper (static web asset at `_content/Loom.Hosting.Web/loom-boot.js`) both the default shell and an embedder's own page can call: `runLoom({ canvas, threads, captureKeys })`
- A default `index.html` + `main.js`, injected into the app's `wwwroot` root only when the app authored none of its own (opt out by authoring your own `wwwroot/index.html`)

## Usage

Reference [`Loom.Sdk`](https://www.nuget.org/packages/Loom.Sdk) as `Sdk="Loom.Sdk"` with `<UseWebGPU>true</UseWebGPU>`; a `browser-wasm` build pulls this host in automatically. The app writes only:

```csharp
LoomApplication.Run(new MyApp(), args);
```

A page of your own calls the boot helper with its own canvas:

```js
import { runLoom } from './_content/Loom.Hosting.Web/loom-boot.js'
await runLoom({ canvas: '#my-canvas' });
```

### Booting the runtime yourself

`runLoom` is the whole contract with the host. A bootstrap that creates the .NET runtime itself must do everything it
does, in order (the header of `loom-boot.js` is the reference):

1. Create the runtime with a thread pool big enough for the app (see [Threads](#threads)).
2. `setModuleImports('loom-webgpu', …)` with the `loom-webgpu.js` module (`_content/Loom.Backend.WebGPU/loom-webgpu.js`).
3. Optionally `setBrowserKeyCapture(false)` on that module (see [Keys the browser would act on](#keys-the-browser-would-act-on)).
4. `runMain()`, whose `LoomApplication.Run` registers the app and returns.
5. Await `StartApp(canvasSelector)` on the `Loom.Hosting.Web.WebAppHost` exports.
6. Call `Frame(timestamp)` once a `requestAnimationFrame`, for as long as the page runs, never again until the last
   call's promise has settled.

`StartApp` starts no loop of its own: without step 6 the app gets no frames. The page's input, resizes and pastes are
queued from `StartApp` on, and each `Frame` takes what was queued since the last.

## Threads

The host works the same on the single-threaded runtime and the multithreaded one (`<WasmEnableThreads>true</WasmEnableThreads>`),
where C# runs on a worker the page's thread may only call asynchronously. So every call from the page into C# is async:
`loom-boot.js` calls the host's `Frame` export once a `requestAnimationFrame` (letting one still running finish rather
than queueing behind it), and the page's input and resizes are queued for `Frame` to take, not called in as they come.
A multithreaded app can then put long work on `Task.Run` and the frame goes on.

The multithreaded runtime runs each thread on a Web Worker from a pool made before the app starts: 16 by default,
`runLoom({ threads: n })` to change it. Every thread the app has running at once, its own, the .NET thread pool's and the
runtime's, must fit in it. A thread started past the pool needs a new worker made while the app runs, and on .NET 11 that
hangs the app without an error: the frame finishes, but the page never hears so and asks for no more. If frames stop
after the app starts more work, raise `threads`, or keep the app's own work to fewer threads.

The multithreaded runtime needs a cross-origin isolated page: the server must send `Cross-Origin-Opener-Policy:
same-origin` and `Cross-Origin-Embedder-Policy: require-corp`. Without them it can't start, and the page says why.

## Keys the browser would act on

While the player is in the canvas (the key's target is the canvas, or the page with nothing else focused), the host
keeps from the browser the keys it would also act on but which mean nothing to an app: F1, F3, F7, F9, F10, Tab and a
lone Alt. In a text field, button or link of the page's own, the browser keeps every key. Shift+Tab, F6 and Shift+F10
are always the browser's, so a keyboard user can leave the page, or the iframe it is embedded in (itch.io's), and reach
the address bar. The app is sent every key either way. To leave every key to the browser, pass `captureKeys: false`:

```js
await runLoom({ canvas: '#loom-canvas', captureKeys: false });
```

## Clipboard

A browser asks the player before a page may read the clipboard, and refuses outright in a cross-origin iframe, but a
paste event carries its text without asking. So `GetClipboardText` here is not the clipboard as it stands, as it is on
the desktop: it is the text of a paste into the page in this frame, and null in any other. Ctrl+V's paste comes in the
same frame as its keydown, so an app that reads the clipboard when it sees Ctrl+V gets what was pasted.
`SetClipboardText` writes the clipboard as the desktop's does, while the input that asked for it is recent.

## Trying it locally

`dotnet run --file tools/loom-serve.cs -- <publish dir> [--port 5080] [--no-isolation]` serves a published app as a host
would: cross-origin isolated (unless `--no-isolation`), with the .NET runtime's files under the right types (a worker
module served as `text/plain` won't start), the publish's precompressed files, and nothing cached.
