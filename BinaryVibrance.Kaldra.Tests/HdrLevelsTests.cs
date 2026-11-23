using BinaryVibrance.Kaldra.Core.Hosting;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// <see cref="HdrLevels"/>: the app's choice outranks what the display reports, which outranks the defaults; the peak
/// never falls below paper white; and a choice that is not a finite luminance above zero is refused.
/// </summary>
public class HdrLevelsTests
{
	[Test]
	public void Paper_white_takes_the_choice_then_the_display_then_the_default()
	{
		HdrLevels.PaperWhite(choice: 300f, display: 240f).ShouldBe(300f);
		HdrLevels.PaperWhite(choice: null, display: 240f).ShouldBe(240f);
		HdrLevels.PaperWhite(choice: null, display: null).ShouldBe(HdrLevels.DefaultPaperWhiteNits);
	}

	[Test]
	public void Peak_takes_the_choice_then_the_display_then_the_default()
	{
		HdrLevels.Peak(choice: 600f, display: 1400f, paperWhite: 200f).ShouldBe(600f);
		HdrLevels.Peak(choice: null, display: 1400f, paperWhite: 200f).ShouldBe(1400f);
		HdrLevels.Peak(choice: null, display: null, paperWhite: 200f).ShouldBe(HdrLevels.DefaultPeakNits);
	}

	[Test]
	public void Peak_is_never_below_paper_white()
	{
		HdrLevels.Peak(choice: 250f, display: null, paperWhite: 400f).ShouldBe(400f);
		HdrLevels.Peak(choice: null, display: 300f, paperWhite: 400f).ShouldBe(400f);
		HdrLevels.Peak(choice: null, display: null, paperWhite: 1200f).ShouldBe(1200f);
	}

	[Test]
	public void A_choice_is_null_or_a_finite_level_above_zero()
	{
		HdrLevels.Choice(null).ShouldBeNull();
		HdrLevels.Choice(203f).ShouldBe(203f);
		foreach (var bad in new[] { 0f, -80f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
			Should.Throw<ArgumentOutOfRangeException>(() => HdrLevels.Choice(bad));
	}
}
