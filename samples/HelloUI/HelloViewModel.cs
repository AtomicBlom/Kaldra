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

	/// <summary>The last key, character, mouse button or controller event the app received.</summary>
	public string LastInput
	{
		get;
		set { if (field != value) { field = value; OnChanged(); } }
	} = "Input: none yet";

	/// <summary>The first controller's sticks and triggers.</summary>
	public string PadState
	{
		get;
		set { if (field != value) { field = value; OnChanged(); } }
	} = "No controller";

	/// <summary>How much of the screen the on-screen keyboard covers.</summary>
	public string Keyboard
	{
		get;
		set { if (field != value) { field = value; OnChanged(); } }
	} = "Keyboard: down";

	/// <summary>A line read from the app's packaged content.</summary>
	public string Greeting
	{
		get;
		set { if (field != value) { field = value; OnChanged(); } }
	} = "";

	/// <summary>How often the app has been suspended, and how long it was away last time.</summary>
	public string Lifecycle
	{
		get;
		set { if (field != value) { field = value; OnChanged(); } }
	} = "Not suspended yet";

	/// <summary>How many times the button has been tapped, kept across the app being ended.</summary>
	public int Taps => _taps;

	/// <summary>Carry on counting from a count saved before the app was last ended.</summary>
	public void RestoreTaps(int taps)
	{
		_taps = taps;
		Count = taps switch { 0 => "Not tapped yet", 1 => "Tapped once", _ => $"Tapped {taps} times" };
	}

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
