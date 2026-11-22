# Verification scripts

Small, dependency-free PowerShell helpers for **rendering verification** — objective, quantitative
signal over a rendered frame without opening it in a viewer (and, for an agent, without pulling the
image bytes into context). They use `System.Drawing` (`LockBits`), so they run anywhere .NET is present.

Golden-image and cross-backend parity are ordinary GPU-gated unit tests (`GalleryGoldenTests` /
`GalleryParityTests` in `Loom.Tests`): they render the gallery demos offscreen in-process and
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

Cross-backend parity bar (`Loom.Gallery.Tests/ImageComparison.cs`): within **4/255
per channel** and **< 0.5%** of pixels over threshold.

## web-capture.ps1

Capture Loom.Web demo frames in a headless browser — the browser half of the WebGPU parity leg. Serves a
published `Loom.Web` from a local `HttpListener`, drives the browser at `?capture=<name>&post`, and saves
the PNG the page POSTs back at frame 60. Unlike the other three this is not an image *inspector*: it
produces the PNGs the others (or `WebGpuParityTests`) then compare.

```powershell
dotnet publish Loom.Web -c Release -o pub
pwsh Scripts/Verification/web-capture.ps1 -PublishDir pub -Names pixelart,bloom -OutDir web-shots
# CAPTURED: pixelart -> web-shots\pixelart.png (113602 bytes)
# CAPTURED: bloom -> web-shots\bloom.png (74413 bytes)
# ADAPTER: qualcomm adreno-7xx
# SUMMARY: captured 2/2 demo(s).
```

`ADAPTER:` names the GPU the browser actually used — check it first when a parity result looks wrong,
because a headless browser can quietly fall back to a software adapter. Exit codes are machine-readable so
a test can tell "this box can't run the leg" from "the leg failed": **0** captured, **2** no browser,
**3** no publish directory, **4** something timed out.

The whole leg (publish + compare against Vulkan) is `pwsh tools/web-parity.ps1`; see
[`docs/webgpu-parity.md`](../../docs/webgpu-parity.md).

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
