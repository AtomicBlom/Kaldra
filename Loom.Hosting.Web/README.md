# Loom.Hosting.Web — browser application host

`Loom.Hosting.Web` is the browser (WebAssembly) host for the [Loom render-graph engine](https://github.com/AtomicBlom/Loom). It brings the WebGPU device up on a page-supplied `<canvas>`, drives a `LoomApplication`'s frame loop through `requestAnimationFrame`, and ships a default full-screen HTML shell — so the same `LoomApplication` that runs on the desktop host runs in the browser with no extra code.

## Requirements

- .NET 10 published as WebAssembly (`dotnet publish -r browser-wasm`)
- `Loom.Backend.WebGPU` (referenced transitively) and a browser with WebGPU support

## What's included

- `WebAppHost` — an `IAppHost` that brings up the device and frame loop; a `[JSExport] StartApp(canvasSelector)` the JS boot helper calls, so the canvas identity flows from the page (an embedder owns their own `<canvas>`)
- `loom-boot.js` — a reusable boot helper (static web asset at `_content/Loom.Hosting.Web/loom-boot.js`) both the default shell and an embedder's own page can call
- A default `index.html` + `main.js`, injected into the app's `wwwroot` root only when the app authored none of its own (opt out by authoring your own `wwwroot/index.html`)

## Usage

Reference [`Loom.Sdk`](https://www.nuget.org/packages/Loom.Sdk) as `Sdk="Loom.Sdk"` with `<UseWebGPU>true</UseWebGPU>`; a `browser-wasm` build pulls this host in automatically. The app writes only:

```csharp
LoomApplication.Run(new MyApp(), args);
```

## Threads

The host works the same on the single-threaded runtime and the multithreaded one (`<WasmEnableThreads>true</WasmEnableThreads>`),
where C# runs on a worker the page's thread may only call asynchronously. So every call from the page into C# is async:
`loom-boot.js` calls the host's `Frame` export once a `requestAnimationFrame` (letting one still running finish rather
than queueing behind it), and the page's input and resizes are queued for `Frame` to take, not called in as they come.
A multithreaded app can then put long work on `Task.Run` and the frame goes on.

The multithreaded runtime needs a cross-origin isolated page: the server must send `Cross-Origin-Opener-Policy:
same-origin` and `Cross-Origin-Embedder-Policy: require-corp`. Without them it can't start, and the page says why.

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
