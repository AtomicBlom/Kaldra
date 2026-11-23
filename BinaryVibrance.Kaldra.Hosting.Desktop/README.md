# BinaryVibrance.Kaldra.Hosting.Desktop — desktop application host

`BinaryVibrance.Kaldra.Hosting.Desktop` is the desktop host for the [Kaldra render-graph engine](https://github.com/AtomicBlom/Kaldra). It creates a Silk.NET window, brings up the engine on whichever Kaldra backend shipped with the app, and drives a `KaldraApplication`'s frame loop — so an app writes only a `KaldraApplication` subclass and one `KaldraApplication.Run(...)` line, no window/device bootstrap.

## Requirements

- .NET 11
- A desktop Kaldra backend present in the app (`BinaryVibrance.Kaldra.Backend.Vulkan` and/or `BinaryVibrance.Kaldra.Backend.DirectX12`) — the host is backend-neutral and selects one at runtime

## What's included

- `DesktopAppHost` — an `IAppHost` that self-registers, owns the Silk.NET window + input pump + blocking frame loop, and branches on the backend name for the window surface (Vulkan `IVkSurface` vs DirectX12 `HWND`)
- `HostOptions` — reusable launch-option parsing every hosted app gets for free (`--backend`, `--vsync`/`--no-vsync`, `--max-fps`, `--hdr`, `--watch-shaders`, `--software-adapter`), passing any unrecognized argument through to the app as `AppArgs`
- On-demand rendering: under `RenderMode.OnDemand` the window is event-driven (sleeps between events) and `Invalidate()` wakes it; `Continuous` (the default) runs the loop every frame

## Usage

Reference [`BinaryVibrance.Kaldra.Sdk`](https://www.nuget.org/packages/BinaryVibrance.Kaldra.Sdk) as `Sdk="BinaryVibrance.Kaldra.Sdk"`; a desktop (non-`browser-wasm`) build pulls this host in automatically. Then:

```csharp
KaldraApplication.Run(new MyApp(), args);
```
