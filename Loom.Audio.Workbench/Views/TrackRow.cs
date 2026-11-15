using System.ComponentModel;

using Loom.Audio.Synth;

using SUIsei.Controls;

namespace Loom.Audio.Workbench.Views;

/// <summary>A track (<c>TrackRow.xaml</c>): mute, solo, gain, a meter, and a <see cref="PlacementRow"/> per part.</summary>
internal sealed partial class TrackRow : ContentControl
{
	private const float MeterWidth = 80f;

	private readonly TrackViewModel _track;
	private bool _showing;

	/// <summary>A row for <paramref name="track"/>.</summary>
	public TrackRow(TrackViewModel track)
	{
		_track = track;
		InitializeComponent();
		Apply();
		track.PropertyChanged += OnChanged;
	}

	partial void OnComponentRebuilt() => Apply();

	private void Apply()
	{
		NameLabel.Text = _track.Name;
		NameLabel.Foreground = Theme.Ink;
		MeterBack.Background = Theme.Off;
		MeterFill.Background = Theme.Meter;
		MuteButton.Checked += (_, _) => { if (!_showing) _track.Muted = true; };
		MuteButton.Unchecked += (_, _) => { if (!_showing) _track.Muted = false; };
		SoloButton.Checked += (_, _) => { if (!_showing) _track.Solo = true; };
		SoloButton.Unchecked += (_, _) => { if (!_showing) _track.Solo = false; };
		GainSlider.ValueChanged += (_, _) => { if (!_showing) _track.GainDb = (float)GainSlider.Value; };
		Placements.Children.Clear();
		foreach (var placement in _track.Placements)
			Placements.Children.Add(new PlacementRow(placement));
		Show();
	}

	private void OnChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(TrackViewModel.Peak))
			ShowMeter();
		else
			Show();
	}

	private void Show()
	{
		_showing = true;
		MuteButton.IsChecked = _track.Muted;
		SoloButton.IsChecked = _track.Solo;
		GainSlider.Value = _track.GainDb;
		_showing = false;
		ShowMeter();
	}

	// The level across 48 dB, silence at the left
	private void ShowMeter() =>
		MeterFill.Width = MeterWidth * MathF.Max(0f, 1f + AudioAnalysis.ToDb(MathF.Max(_track.Peak, 1e-6f)) / 48f);
}
