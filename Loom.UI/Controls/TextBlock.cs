namespace Loom.UI.Controls;

/// <summary>
/// A run of text (PlanningUI.md §D2.1 / §D3). Phase B participates in two-pass layout with a <b>placeholder</b>
/// metric (no shaping yet): width ≈ <c>Text.Length · FontSize · 0.5</c>, height ≈ <c>FontSize · 1.2</c>. Real MSDF
/// shaping (HarfBuzz) + glyph-run composition land in MU1/MU5; until then the element measures and lays out but
/// emits no glyph content, so the draw stream stays empty for text.
/// </summary>
public sealed class TextBlock : FrameworkElement
{
	public static readonly DependencyProperty<TextBlock, string> TextProperty =
		DependencyProperty.Register<TextBlock, string>(nameof(Text),
			new PropertyMetadata<TextBlock, string> { DefaultValue = "", Invalidates = InvalidationFlags.Measure });

	public static readonly DependencyProperty<TextBlock, float> FontSizeProperty =
		DependencyProperty.Register<TextBlock, float>(nameof(FontSize),
			new PropertyMetadata<TextBlock, float> { DefaultValue = 14f, Invalidates = InvalidationFlags.Measure });

	/// <summary>The text to lay out (placeholder metrics until MSDF shaping, MU1).</summary>
	public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

	/// <summary>Font size in DIPs.</summary>
	public float FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

	/// <summary>Glyph colour (consumed once glyph composition lands in MU1).</summary>
	public HdrColor Foreground { get; set; } = HdrColor.FromRgb(255, 255, 255);

	// Placeholder metric: a fixed average-advance estimate so layout is exercised before real shaping (MU1).
	private const float AvgAdvanceFactor = 0.5f;
	private const float LineHeightFactor = 1.2f;

	protected override Size MeasureOverride(Size available)
	{
		var text = Text;
		if (string.IsNullOrEmpty(text))
			return new Size(0f, FontSize * LineHeightFactor);
		var width = MathF.Min(text.Length * FontSize * AvgAdvanceFactor, available.Width);
		return new Size(width, FontSize * LineHeightFactor);
	}
}
