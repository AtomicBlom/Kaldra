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
