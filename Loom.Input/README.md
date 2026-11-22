# Loom.Input

The input for the [Loom engine](https://github.com/AtomicBlom/Loom), apart from any device's API. It depends only on
the BCL, so the desktop, Android and browser hosts and headless tests all share it.

- **Events:** hosts turn what their platform reports (keys, the mouse, pads, touches) into one `InputEvent` stream.
- **A snapshot a frame:** `InputSnapshot`, what is down, pressed and released this frame, where the pointer and every
  touch are, and the device the player last used.
- **Actions:** declarative action maps (buttons, axes, vectors) over keys, buttons, sticks and touches, with
  deadzones and response curves, rebound by pressing new controls (`BindingCapture`) and saved as JSON.
- **Layers:** a stack that decides which layer (a UI, a menu, the game) gets each input.
- **Rumble:** a queue the game posts to and the host plays out on the pad.

A `LoomApplication` reads the frame's snapshot as `Input`. Loom's `docs/api-vocabulary.md`, "Input", lists the types.
