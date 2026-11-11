# HelloUI: one Loom app on the desktop and on Android

A SUIsei XAML page (a tap counter, a slider, a checkbox, and the GPU and runtime it is on) over a backdrop drawn by the app's own Slang shader. The same `HelloUIApp` builds two ways, chosen by target framework:

| Target | Output | Host |
|---|---|---|
| `net10.0` | a desktop exe | `Loom.Hosting.Desktop` (GLFW window) |
| `net11.0-android` | an APK (CoreCLR) | `Loom.Hosting.Android` (`LoomActivity`) |

Only the entry point differs: `Platforms/Desktop/Program.cs`, or `Platforms/Android/MainActivity.cs`. The page lays out at `LoomApplication.DisplayScale`, so it keeps one physical size on a 100% monitor, a 200% tablet and a phone.

## Desktop

```bash
dotnet run --project samples/HelloUI -f net10.0
```

## Android

Needs the `android` workload (`dotnet workload install android`) and a device with **Vulkan 1.3** (most phones from Android 13 on; the manifest declares it, so a store would keep the app off others). Turn on USB debugging on the phone, connect it, and check it is listed:

```bash
adb devices
```

**Debug build, installed and started in one step.** Debug uses fast deployment, so the assemblies are pushed beside the APK rather than inside it. Install it this way, not with `adb install`:

```bash
dotnet build samples/HelloUI -f net11.0-android -t:Run
```

**Release build** (trimmed, ReadyToRun, everything inside the APK), installed with adb:

```bash
dotnet build samples/HelloUI -f net11.0-android -c Release
```

```bash
adb install -r samples/HelloUI/bin/Release/net11.0-android/com.atomicblom.loom.helloui-Signed.apk
```

**Logs.** The host logs under the tag `Loom` (bring-up, device, size, display scale, and any failure), and the engine's own log lines arrive on the app's stdout:

```bash
adb logcat -v brief Loom:V DOTNET:V AndroidRuntime:E vulkan:V *:S
```

A failure is also shown on the screen in place of the frame, with its stack trace.

## What to check on a device

- It starts and shows the card centred over the gradient, at a comfortable physical size: the card is 360 layout units wide, about 6 cm.
- Tapping **Tap me** counts. Dragging the slider moves it and updates its label. The checkbox toggles.
- Rotating the phone resizes and re-centres the page. The page stays upright: the compositor rotates the frame. Pre-rotation, which would save that compositor pass, is not done yet.
- Pressing Home and coming back resumes where it was, with the counter kept. The engine let its surface go and took the new one (`IReattachableSurface`); the log shows `surface detached` and `surface attached`.
- The device line names the phone's GPU, and the runtime line says `.NET 11` on `Arm64`.
