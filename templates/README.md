# Loom.Templates — `dotnet new` templates for Loom

Scaffold a cross-platform [Loom](https://github.com/AtomicBlom/Loom) app that builds to desktop or browser from one project.

## Install

```
dotnet new install Loom.Templates
```

## Use

```
dotnet new loom -n MyGame
cd MyGame
dotnet build                        # native desktop exe (Vulkan)
dotnet publish -r browser-wasm      # WebGPU browser app (serve the published wwwroot)
```

The same source runs on both — the RID selects the base SDK, backend, and host. The scaffolded project is one `Program.cs` (a `LoomApplication` subclass), a Slang shader, and a `global.json` pinning the Loom SDK; there is no window, canvas, or platform code to write.

## Templates

| Short name | Description |
|---|---|
| `loom` | A minimal cross-platform Loom application (renders a triangle). |
