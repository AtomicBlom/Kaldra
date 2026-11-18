# Loom.Hosting.Desktop — desktop application host

`Loom.Hosting.Desktop` is the desktop host for the [Loom render-graph engine](https://github.com/AtomicBlom/Loom). It creates a Silk.NET window, brings up the engine on whichever Loom backend shipped with the app, and drives a `LoomApplication`'s frame loop — so an app writes only a `LoomApplication` subclass and one `LoomApplication.Run(...)` line, no window/device bootstrap.

## Requirements

- .NET 11
- A desktop Loom backend present in the app (`Loom.Backend.Vulkan` and/or `Loom.Backend.DirectX12`) — the host is backend-neutral and selects one at runtime

## What's included

- `DesktopAppHost` — an `IAppHost` that self-registers, owns the Silk.NET window + input pump + blocking frame loop, and branches on the backend name for the window surface (Vulkan `IVkSurface` vs DirectX12 `HWND`)
- `HostOptions` — reusable launch-option parsing every hosted app gets for free (`--backend`, `--vsync`/`--no-vsync`, `--max-fps`, `--hdr`, `--watch-shaders`, `--software-adapter`), passing any unrecognized argument through to the app as `AppArgs`
- On-demand rendering: under `RenderMode.OnDemand` the window is event-driven (sleeps between events) and `Invalidate()` wakes it; `Continuous` (the default) runs the loop every frame

## Usage

Reference [`Loom.Sdk`](https://www.nuget.org/packages/Loom.Sdk) as `Sdk="Loom.Sdk"`; a desktop (non-`browser-wasm`) build pulls this host in automatically. Then:

```csharp
LoomApplication.Run(new MyApp(), args);
```
