namespace Loom.Core.Hosting;

/// <summary>
/// The constant-step timing handed to <see cref="LoomApplication.FixedUpdate"/> — <see cref="DeltaSeconds"/> is
/// always <c>1 / AppOptions.FixedStepHz</c> regardless of the display's actual frame rate, and
/// <see cref="TotalSeconds"/> accumulates in those fixed steps rather than tracking wall-clock time. Use it for
/// simulation code (physics, deterministic gameplay ticks) that must not vary with the render frame rate; render
/// state that needs to move smoothly between two fixed states reads <see cref="FrameTime.Alpha"/> instead.
/// </summary>
public readonly record struct FixedTime(double DeltaSeconds, double TotalSeconds);
