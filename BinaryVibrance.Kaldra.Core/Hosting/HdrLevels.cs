using System.Runtime.CompilerServices;

namespace BinaryVibrance.Kaldra.Core.Hosting;

/// <summary>
/// How an engine's HDR paper white and peak are resolved from the app's choice and what the display reports: the one
/// place the fallbacks live, which <see cref="IEngine"/>'s default members and every engine use alike.
/// </summary>
public static class HdrLevels
{
	/// <summary>Paper white, in nits, where neither the app nor the display says.</summary>
	public const float DefaultPaperWhiteNits = 200f;

	/// <summary>The peak, in nits, where neither the app nor the display says.</summary>
	public const float DefaultPeakNits = 1000f;

	/// <summary>The app's choice, else the display's SDR white, else <see cref="DefaultPaperWhiteNits"/>.</summary>
	public static float PaperWhite(float? choice, float? display) => choice ?? display ?? DefaultPaperWhiteNits;

	/// <summary>The app's choice, else the display's peak, else <see cref="DefaultPeakNits"/>; never below
	/// <paramref name="paperWhite"/>, since highlights cannot roll off toward a level under white.</summary>
	public static float Peak(float? choice, float? display, float paperWhite) =>
		MathF.Max(choice ?? display ?? DefaultPeakNits, paperWhite);

	/// <summary>
	/// An app's choice of a level, checked: null (follow the display) or a finite luminance above zero. Anything else
	/// throws rather than lighting the frame by it.
	/// </summary>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="nits"/> is zero, negative, infinite or NaN.</exception>
	public static float? Choice(float? nits, [CallerArgumentExpression(nameof(nits))] string? name = null)
	{
		if (nits is { } level && !(float.IsFinite(level) && level > 0f))
			throw new ArgumentOutOfRangeException(name, level, "An HDR level is a finite luminance in nits above zero, or null to follow the display.");
		return nits;
	}
}
