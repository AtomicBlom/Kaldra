namespace BinaryVibrance.Kaldra.Core.Hosting;

/// <summary>
/// The MSAA sample counts a device can render to. Each flag's value <em>is</em> the sample count, so a declared
/// domain tests directly against it — <c>(capabilities.SampleCounts &amp; (SampleCounts)domain.Samples) != 0</c> —
/// rather than mapping a count onto an ordinal first.
/// </summary>
[Flags]
public enum SampleCounts
{
	/// <summary>Nothing is known about the device's sample support.</summary>
	None = 0,

	/// <summary>Single-sampled. Every device supports this.</summary>
	X1 = 1,

	/// <summary>2× multisampling.</summary>
	X2 = 2,

	/// <summary>4× multisampling.</summary>
	X4 = 4,

	/// <summary>8× multisampling.</summary>
	X8 = 8,

	/// <summary>16× multisampling.</summary>
	X16 = 16,
}
