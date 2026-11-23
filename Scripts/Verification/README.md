# Verification scripts

Small, dependency-free PowerShell helpers for **rendering verification** — objective, quantitative
signal over a rendered frame without opening it in a viewer (and, for an agent, without pulling the
image bytes into context). They use `System.Drawing` (`LockBits`), so they run anywhere .NET is present.

Golden-image and cross-backend parity are ordinary GPU-gated unit tests (`GalleryGoldenTests` /
`GalleryParityTests` in Kaldra.Gallery's `Kaldra.Gallery.Tests`): they render the gallery demos offscreen in-process and
compare them, with no window or capture flags. These helpers are for **ad-hoc** inspection of any PNG
you have on hand — a test-produced capture, a one-off `IEngine.RequestCapture` output, a bug repro.

## colorcount.ps1

Count the distinct RGB colours in a PNG (alpha ignored). A fast "did anything actually render?"
check — a black/empty frame is ~1 colour; a real scene is thousands.

```powershell
pwsh Scripts/Verification/colorcount.ps1 -Path capture.png
# capture.png: 4213 distinct RGB colours (1280x720)
```

## diff.ps1

Pixel-diff two same-size PNGs (e.g. a Vulkan capture vs a DX12 capture of the same demo). Prints
the mean and max per-channel absolute difference and the fraction of pixels differing by more
than `-Threshold`, and writes a heatmap PNG (brighter = more different). `-CropTop` skips the top
HUD strip (the ImGui bar differs harmlessly between backends — title/device/timing text).

```powershell
pwsh Scripts/Verification/diff.ps1 -A vk.png -B dx12.png -Out diff.png
# vk.png vs dx12.png: mean=0.31/255  max=12  pixels>16: 0.0%  -> diff.png
```

The gallery's cross-backend parity bar: within **4/255
per channel** and **< 0.5%** of pixels over threshold.

The browser capture scripts for the WebGPU parity leg are in [Kaldra.Gallery](https://github.com/AtomicBlom/Kaldra.Gallery)'s `Scripts/Verification`.

## coverage.ps1

Where did content land? Reports the non-black bounding box and an NxN grid distribution. `colorcount`
tells you *whether* something rendered; this tells you *where* — and "everything is in one corner"
is the signature of a camera / transform bug (vs a shading bug, which keeps full-frame coverage).
Skips the top HUD strip (`-CropTop`).

```powershell
pwsh Scripts/Verification/coverage.ps1 -Path capture.png
# capture.png: 39.7% content (1280x720, skipped top 28px)
#   bbox: x[28..1251] y[28..699]  (1224x672 px)
#   3x3 grid (% of each cell that is content):
#       51.1   41.0   47.6
#       34.7   29.1   34.6
#       41.6   35.6   41.5
# A broken render with everything in cell (0,0) and the rest 0.0 == a transform/camera bug, not a shader one.
```

> Cross-backend parity is enforced automatically by `GalleryParityTests` (Vulkan vs DirectX12,
> offscreen, in-process). Reach for `diff.ps1` when you want to eyeball *where* two frames disagree
> (its heatmap) or to check a PNG pair the test suite doesn't cover.

> For an **"is it rendered correctly"** judgment (vs the objective counts above), hand the PNG to a
> subagent with a detailed description of the expected image and have it report pass/fail — images
> are context-heavy (see `CLAUDE.md` → Agent Behaviour).
