# BinaryVibrance.Kaldra.Hosting.Android

The Android host for the [Kaldra engine](https://github.com/AtomicBlom/Kaldra): a `KaldraActivity` whose `SurfaceView` carries
the Vulkan swapchain, frames paced by the Choreographer on the UI thread, and touches, keys and pads fed into the app's
input. It constructs its one backend, Vulkan, directly.

```csharp
[Activity(MainLauncher = true, ConfigurationChanges = KaldraActivity.HandledConfigChanges, Theme = KaldraActivity.FullscreenTheme)]
public sealed class MainActivity : KaldraActivity
{
    protected override KaldraApplication CreateApplication() => new MyGame();
}
```

It needs Android 10 and a device with Vulkan 1.3. An app built with the Kaldra SDK for an `android-*` runtime identifier
takes it on its own.
