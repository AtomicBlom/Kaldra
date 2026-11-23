# BinaryVibrance.Kaldra.Templates — `dotnet new` templates for Kaldra

Scaffold a cross-platform [Kaldra](https://github.com/AtomicBlom/Kaldra) app that builds to desktop or browser from one project.

## Install

```
dotnet new install BinaryVibrance.Kaldra.Templates
```

## Use

```
dotnet new kaldra -n MyGame
cd MyGame
dotnet build                        # native desktop exe (Vulkan)
dotnet publish -r browser-wasm      # WebGPU browser app (serve the published wwwroot)
```

The same source runs on both — the RID selects the base SDK, backend, and host. The scaffolded project is one `Program.cs` (a `KaldraApplication` subclass), a Slang shader, and a `global.json` pinning the Kaldra SDK; there is no window, canvas, or platform code to write.

## Templates

| Short name | Description |
|---|---|
| `kaldra` | A minimal cross-platform Kaldra application (renders a triangle). |
