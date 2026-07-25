namespace Loom.Core.Hosting;

/// <summary>
/// How the host paces a <see cref="LoomApplication"/>'s frame submission.
/// </summary>
public enum RenderMode
{
	/// <summary>
	/// Submit a frame every tick — the default, and what a game wants: the scene changes continuously, so there is
	/// always something new to present.
	/// </summary>
	Continuous,

	/// <summary>
	/// Submit only when the app asks (<see cref="LoomApplication.Invalidate"/>), on the first frame, or after a
	/// resize. An idle app submits nothing and the compositor holds the last presented image; the desktop host also
	/// sleeps until an event arrives rather than spinning. Suits editors, tools, and UI-driven apps that redraw on
	/// change rather than on a clock. Submission is whole-frame — there is no partial/region redraw (the engine
	/// aliases transient targets across frames, so a skipped frame preserves only the presented image, not the
	/// intermediates).
	/// </summary>
	OnDemand,
}
