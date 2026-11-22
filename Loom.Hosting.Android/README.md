# Loom.Hosting.Android

The Android host for the [Loom engine](https://github.com/AtomicBlom/Loom): a `LoomActivity` whose `SurfaceView` carries
the Vulkan swapchain, frames paced by the Choreographer on the UI thread, and touches, keys and pads fed into the app's
input. It constructs its one backend, Vulkan, directly.

```csharp
[Activity(MainLauncher = true, ConfigurationChanges = LoomActivity.HandledConfigChanges, Theme = LoomActivity.FullscreenTheme)]
public sealed class MainActivity : LoomActivity
{
    protected override LoomApplication CreateApplication() => new MyGame();
}
```

It needs Android 10 and a device with Vulkan 1.3. An app built with the Loom SDK for an `android-*` runtime identifier
takes it on its own.
