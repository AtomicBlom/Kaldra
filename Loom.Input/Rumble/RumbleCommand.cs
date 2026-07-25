namespace Loom.Input;

/// <summary>
/// A request to vibrate a gamepad. Frequencies are normalised [0, 1] intensities for the two motors most pads
/// expose — <see cref="LowFrequency"/> drives the heavy/low-frequency motor, <see cref="HighFrequency"/> the
/// light/high-frequency motor — held for <see cref="Seconds"/>. A host adapter drains these to hardware; where
/// vibration is unsupported it is a no-op.
/// </summary>
/// <param name="PadIndex">The gamepad to rumble.</param>
/// <param name="LowFrequency">Heavy-motor intensity, [0, 1].</param>
/// <param name="HighFrequency">Light-motor intensity, [0, 1].</param>
/// <param name="Seconds">How long to sustain the effect.</param>
public readonly record struct RumbleCommand(int PadIndex, float LowFrequency, float HighFrequency, float Seconds);
