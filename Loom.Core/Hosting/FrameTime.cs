namespace Loom.Core.Hosting;

/// <summary>
/// Per-frame timing handed to <see cref="LoomApplication.Update"/> and <see cref="LoomApplication.Draw"/> —
/// the same two values the engine already carries on <see cref="FrameInputs"/> (<see cref="DeltaSeconds"/>
/// = <see cref="FrameInputs.DeltaSeconds"/>, <see cref="TotalSeconds"/> = <see cref="FrameInputs.Time"/>),
/// surfaced to the app so a per-frame hook needs no other clock. Kept minimal: a later fixed-timestep design
/// adds a new hook rather than changing this shape.
/// </summary>
public readonly record struct FrameTime(double DeltaSeconds, double TotalSeconds);
