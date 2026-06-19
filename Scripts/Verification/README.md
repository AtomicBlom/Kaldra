# Verification scripts

Small, dependency-free PowerShell helpers for **headless rendering verification** — objective,
quantitative signal over a captured frame without opening it in a viewer (and, for an agent,
without pulling the image bytes into context). They use `System.Drawing` (`LockBits`), so they
run anywhere .NET is present.

The capture half of the loop is the sandbox itself: `Loom.Sandbox` honours `LOOM_CAPTURE=path`
(grab one frame to a PNG with frozen time + a fixed delta) and `LOOM_SMOKE_FRAMES=N` (render N
frames, then exit). With `LOOM_BACKEND=DX12` and `LOOM_DEMO=<name|index>` you can capture any
demo on either backend deterministically — the inputs for a cross-backend pixel diff.

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

Cross-backend parity bar (PlanningPhase2 §6 / `Loom.Tests/ImageComparison.cs`): within **4/255
per channel** and **< 0.5%** of pixels over threshold.

## Typical loop

```powershell
$S = "$env:TEMP/loom-caps"; mkdir $S -Force | Out-Null
LOOM_DEMO=Sprites LOOM_CAPTURE="$S/vk.png"   LOOM_SMOKE_FRAMES=70 dotnet run --project Loom.Sandbox
$env:LOOM_BACKEND="DX12"
LOOM_DEMO=Sprites LOOM_CAPTURE="$S/dx12.png" LOOM_SMOKE_FRAMES=70 dotnet run --project Loom.Sandbox
pwsh Scripts/Verification/diff.ps1 -A "$S/vk.png" -B "$S/dx12.png" -Out "$S/diff.png"
```

> For an **"is it rendered correctly"** judgment (vs the objective counts above), hand the PNG to a
> subagent with a detailed description of the expected image and have it report pass/fail — images
> are context-heavy (see `CLAUDE.md` → Agent Behaviour).
