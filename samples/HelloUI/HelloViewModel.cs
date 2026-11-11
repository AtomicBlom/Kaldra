using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HelloUI;

/// <summary>
/// What <c>MainPage</c> shows: the GPU and runtime the app is on, a tap counter and the slider's value, each raising
/// <see cref="INotifyPropertyChanged"/> so the page's <c>{x:Bind …, Mode=OneWay}</c> labels follow it.
/// </summary>
internal sealed class HelloViewModel : INotifyPropertyChanged
{
	private int _taps;

	public string Device
	{
		get;
		set { if (field != value) { field = value; OnChanged(); } }
	} = "Starting…";

	public string Platform { get; } = $"{RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture}";

	public string Runtime { get; } = RuntimeInformation.FrameworkDescription;

	public string Count
	{
		get;
		private set { if (field != value) { field = value; OnChanged(); } }
	} = "Not tapped yet";

	public string SliderText
	{
		get;
		private set { if (field != value) { field = value; OnChanged(); } }
	} = "Slider: 50";

	public void Tap()
	{
		_taps++;
		Count = _taps == 1 ? "Tapped once" : $"Tapped {_taps} times";
	}

	public void SetSlider(double value) => SliderText = $"Slider: {value:0}";

	public event PropertyChangedEventHandler? PropertyChanged;

	private void OnChanged([CallerMemberName] string? name = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
