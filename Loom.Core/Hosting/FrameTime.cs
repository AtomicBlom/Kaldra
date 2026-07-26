namespace Loom.Core.Hosting;

/// <summary>
/// Per-frame timing handed to <see cref="LoomApplication.Update"/> and <see cref="LoomApplication.BeforeRender"/> —
/// the same two values the engine already carries on <see cref="FrameInputs"/> (<see cref="DeltaSeconds"/>
/// = <see cref="FrameInputs.DeltaSeconds"/>, <see cref="TotalSeconds"/> = <see cref="FrameInputs.Time"/>),
/// surfaced to the app so a per-frame hook needs no other clock. <see cref="Alpha"/> is the render-interpolation
/// fraction between the last two <see cref="FixedTime"/> steps — 0 when <see cref="AppOptions.FixedStepHz"/> is
/// disabled (the default); blend the previous and current fixed-update state by it when rendering something
/// whose position only advances in <see cref="LoomApplication.FixedUpdate"/>.
/// </summary>
public readonly record struct FrameTime(double DeltaSeconds, double TotalSeconds, float Alpha = 0f);
