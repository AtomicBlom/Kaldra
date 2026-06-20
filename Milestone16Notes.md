# Milestone 16 — implementation notes & decisions

A running log of design decisions, judgment calls, and problems encountered while building
M16 (Track C: large-world realistic). For review. The authoritative *what* is
`PlanningPhase2.md §5`; the board (#6) is live status; this file records the *why* behind
the choices made during implementation.

## Working agreement (this milestone)
- On any problem: go with best intuition, choosing solutions that align with the engine's
  design principles (`PlanningPhase2.md §6` / `Planning.md §8`): derived-not-declared,
  backend isolation (Vulkan + DirectX12 at parity, no API type in Core/Graph), you-hand-me-
  draw-lists, pure per-frame closures, every feature ships its debug view.
- Commit at each checkpoint (a buildable, ideally parity-verified slice).
- Record every non-obvious decision here.

## Resolved gating decisions
- **Terrain representation (DECIDE #5):** Heightmap clipmap / CDLOD (continuous-LOD
  heightfield), NOT voxel/SDF. Surface-only — no caves/overhangs/dig-through. Fits
  planet-from-orbit + erosion-as-displacement. Gates M16-3, M16-4.
- **GPU-driven culling + precision (DECIDE #6):** Indirect + Hi-Z occlusion culling, plus
  camera-relative ("floating origin") transforms. Lands the indirect-draw path deferred from
  M12. Gates M16-5, M16-7.

## Execution order (per the approved roadmap)
M16-1 → M16-2 → M16-3 → M16-4 → M16-5 → M16-7 → M16-6 → M16-8 → M16-10 → M16-9 → M16-11 → M16-12.

---

## Log

### M16-1.0 — enabling slice (cubemaps, float/HDR textures, glTF PBR material extraction)
*(in progress)*

**Checkpoint 1 — glTF PBR material extraction (`Loom.Assets`).** `ModelMaterial` previously surfaced
only base-colour + normal; metallic-roughness, occlusion, and emissive were silently dropped even
though glTF's native material model IS metallic-roughness. Extended `ModelMaterial` with those three
textures plus the scalar PBR factors (base-colour, metallic, roughness, emissive, occlusion strength,
normal scale), and read them in `GltfLoader.ResolveMaterial`.
- Factors read via SharpGLTF 1.0.3 `MaterialChannel`: colour factors from `.Color` (Vector4), scalars
  from a `Parameters` name/value scan (`Factor(...)` helper) so no version-fragile key string can throw.
- Decision: a material still emits **null when it carries no texture at all**, preserving the
  texture-bundle contract earlier consumers rely on (a factor-only material would otherwise start
  binding into texture-sampling techniques that expect images). Factors ride along only with textures
  for now; the PBR consumer (M16-1) decides how to treat factor-only materials when it lands.
- Verified: `Loom.Assets` builds clean; `GltfSkinningLoadTests` 4/4 pass (no regression to `cube.gltf`
  / `rigged_bar.glb` loading). The new properties read as "unused" until the PBR shader consumes them.

**Checkpoint 2 — float/HDR textures (the IBL environment source).** Added `TextureFormat.Rgba16F`,
`TextureData.FromRgba16F(width, height, ReadOnlySpan<Half>)` (single level — HDR sources ship no mip
chain; the GPU prefilter makes the roughness mips), and `ImageLoader.LoadHdr/DecodeHdr` (StbImageSharp
`ImageResultFloat` → float→half). Both texture stores map `Rgba16F` to their native half format
(`R16G16B16A16Sfloat` / `FormatR16G16B16A16Float`).
- Decision: store HDR as **16-bit half**, not 32-bit float — IBL precision is ample and it halves
  memory for a multi-MB equirect map; it also matches the engine's existing `Rgba16F` HDR storage.
- Fixed a latent assumption in `DirectX12TextureStore` upload: `srcPitch` was hardcoded `Width*4`
  (RGBA8). Now derived as `Pixels.Length / Height` — format-agnostic, so the 8-byte RGBA16F rows
  upload correctly (and any future bytes-per-texel just works). Vulkan's upload was already
  format-agnostic (copies the level bytes wholesale; the image format drives the layout).
- Verified: full solution builds clean (Vulkan + DirectX12); `TextureDataTests` 17/17 (incl. 2 new
  RGBA16F tests). The byte-for-byte HDR→GPU path is exercised on the GPU in checkpoint 3 (cubemaps).

**Checkpoint 3a — cube `TextureData` (Core).** `TextureData.FromCubeFaces(size, faces, format, mips)`
plus `IsCube`/`LayerCount`/`MipsPerFace`. Six faces (+X,−X,+Y,−Y,+Z,−Z) each mipped independently with
the existing gamma-correct box filter.
- Key layout decision: levels are stored **face-major** — `Levels[face*MipsPerFace + mip]`. That index
  equals D3D12's subresource index formula (`mip + arraySlice*MipLevels`) exactly, so the DX12 upload
  loop needs no reshuffle, and Vulkan just sets `baseArrayLayer = i / MipsPerFace`,
  `mipLevel = i % MipsPerFace`. One layout serves both backends.
- Per-face (not cross-face-edge) mip filtering — the standard, deterministic, backend-identical choice.
- Verified: Core builds; `TextureDataTests` 20/20 (+3 cube tests). Backend cube create/view/upload is
  checkpoints 3b (Vulkan) + 3c (DirectX12).
