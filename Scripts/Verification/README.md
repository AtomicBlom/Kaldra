# Verification scripts

Small, dependency-free PowerShell helpers for **headless rendering verification** — objective,
quantitative signal over a captured frame without opening it in a viewer (and, for an agent,
without pulling the image bytes into context). They use `System.Drawing` (`LockBits`), so they
run anywhere .NET is present.

The capture half of the loop is the sandbox itself, configured by command-line options (no `LOOM_*`
env vars — run `dotnet run --project Loom.Sandbox -- --help` for the full list): `--capture <path>`
grabs one frame to a PNG with frozen time + a fixed delta then exits, `--backend <vulkan|dx12>`
selects the backend, and `--demo <name|index>` picks the demo — the inputs for a cross-backend diff.

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

## capture-compare.ps1

The whole loop in one command: capture a demo on **both** backends (headless, frozen time), then
`colorcount` + `coverage` + `diff` each. Pass the **exact demo name**. Builds `Loom.Sandbox` once
unless `-NoBuild`. A dev-box convenience (it spawns the windowed sandbox); CI uses the xUnit golden
harness instead.

```powershell
pwsh Scripts/Verification/capture-compare.ps1 -Demo "Sprites + Atlas"
# -> $env:TEMP/loom-verify/Sprites___Atlas-{vk,dx12,diff}.png  + counts/coverage/diff to stdout
```

## Manual loop (what capture-compare automates)

```powershell
$S = "$env:TEMP/loom-caps"; mkdir $S -Force | Out-Null
dotnet run --project Loom.Sandbox -- --demo "Sprites + Atlas" --capture "$S/vk.png"
dotnet run --project Loom.Sandbox -- --demo "Sprites + Atlas" --capture "$S/dx12.png" --backend dx12
pwsh Scripts/Verification/diff.ps1 -A "$S/vk.png" -B "$S/dx12.png" -Out "$S/diff.png"
```

> Demo selection (`--demo`) takes a 0-based index, an exact (case-insensitive) name, or a **unique
> case-insensitive substring** (e.g. `bloom`). An ambiguous substring lists its matches and a non-match
> lists every demo — either way the sandbox exits rather than guessing, so a typo fails loudly.

> For an **"is it rendered correctly"** judgment (vs the objective counts above), hand the PNG to a
> subagent with a detailed description of the expected image and have it report pass/fail — images
> are context-heavy (see `CLAUDE.md` → Agent Behaviour).
