namespace Loom.Core.Hosting;

/// <summary>
/// How far in from each edge of the app's surface something covers it, in surface pixels: the parts of a phone's screen
/// a camera cutout hides, for <see cref="LoomApplication.SafeAreaInsets"/>. Zero on every edge is a surface nothing covers.
/// </summary>
public readonly record struct ScreenInsets(float Left, float Top, float Right, float Bottom)
{
	/// <summary>No inset on any edge.</summary>
	public static ScreenInsets None => default;
}
