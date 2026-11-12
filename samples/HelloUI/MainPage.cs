using SUIsei;
using SUIsei.Media;
using SUIsei.RoutedEvents;

namespace HelloUI;

/// <summary>
/// Code-behind for <c>MainPage.xaml</c>: the generator writes the other half (typed fields, <c>InitializeComponent</c>,
/// the <c>{x:Bind}</c> glue and the click wire); this half supplies the view model, the handlers and the colours.
/// </summary>
internal sealed partial class MainPage
{
	public HelloViewModel ViewModel { get; } = new();

	public MainPage()
	{
		InitializeComponent();
		Card.Background = new SolidColorBrush(HdrColor.FromRgb(24, 30, 46, 230));
		TitleLabel.Foreground = new SolidColorBrush(HdrColor.FromRgb(180, 210, 255));
		DeviceLabel.Foreground = new SolidColorBrush(HdrColor.FromRgb(150, 160, 185));
		PlatformLabel.Foreground = new SolidColorBrush(HdrColor.FromRgb(150, 160, 185));
		RuntimeLabel.Foreground = new SolidColorBrush(HdrColor.FromRgb(150, 160, 185));
		TapButton.Background = new SolidColorBrush(HdrColor.FromRgb(50, 100, 200));
		ValueSlider.ValueChanged += (_, _) => ViewModel.SetSlider(ValueSlider.Value);
	}

	private void OnTapClick(object sender, RoutedEventArgs e) => ViewModel.Tap();
}
