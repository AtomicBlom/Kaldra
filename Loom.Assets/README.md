# Loom.Assets — neutral asset I/O

`Loom.Assets` provides image decoding, glTF model loading, and CPU mip-chain generation for the [Loom render-graph engine](https://github.com/AtomicBlom/Loom), with no reference to any graphics API.

## What's included

| Feature | Detail |
|---|---|
| Image decoding | `StbImageSharp` — PNG, JPEG, BMP, TGA, HDR → `TextureData` |
| CPU mip generation | Deterministic box-filter mip chain; byte-identical output on Vulkan and DirectX 12 |
| glTF loading | `SharpGLTF` → neutral `MeshData` / `Vertex` (pos + normal + uv + tangent) |
| `Vertex` | The canonical engine vertex type; carries tangent for normal mapping |

## Usage

```csharp
// Load a texture (all mips built on CPU)
TextureData tex = ImageLoader.Load("albedo.png");

// Load a glTF scene
GltfModel model = GltfLoader.Load("character.glb");
```

The engine backends (`Loom.Backend.Vulkan`, `Loom.Backend.DirectX12`) accept `TextureData` and `MeshData` directly and upload them to the GPU.

## No GPU dependency

`Loom.Assets` contains only managed code and runs headless — useful in content pipelines, asset processors, and server-side build tools that need to inspect or transform assets without a GPU.
