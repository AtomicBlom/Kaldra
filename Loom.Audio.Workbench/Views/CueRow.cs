using System.ComponentModel;
using System.Globalization;

using SUIsei;
using SUIsei.Controls;

namespace Loom.Audio.Workbench.Views;

/// <summary>A cue (<c>CueRow.xaml</c>): a slider for a level, a button per option for a choice.</summary>
internal sealed partial class CueRow : ContentControl
{
	private readonly CueViewModel _cue;
	private readonly List<ToggleButton> _options = [];
	private bool _showing;

	/// <summary>A row for <paramref name="cue"/>.</summary>
	public CueRow(CueViewModel cue)
	{
		_cue = cue;
		InitializeComponent();
		Apply();
		cue.PropertyChanged += OnChanged;
	}

	partial void OnComponentRebuilt() => Apply();

	private void Apply()
	{
		NameLabel.Text = _cue.Name;
		NameLabel.Foreground = Theme.Ink;
		ValueLabel.Foreground = Theme.Dim;
		var choice = _cue.IsChoice;
		ValueSlider.Visibility = choice ? Visibility.Collapsed : Visibility.Visible;
		ValueLabel.Visibility = choice ? Visibility.Collapsed : Visibility.Visible;
		Options.Visibility = choice ? Visibility.Visible : Visibility.Collapsed;
		ValueSlider.ValueChanged += (_, _) => { if (!_showing) _cue.Value = (float)ValueSlider.Value; };
		Options.Children.Clear();
		_options.Clear();
		for (var i = 0; i < _cue.Options.Count; i++)
		{
			var index = i;
			var button = Make.Toggle(_cue.Options[i]);
			button.Click += (_, _) => { _cue.SelectedOption = index; Show(); };
			_options.Add(button);
			Options.Children.Add(button);
		}
		Show();
	}

	private void OnChanged(object? sender, PropertyChangedEventArgs e) => Show();

	private void Show()
	{
		_showing = true;
		ValueSlider.Value = _cue.Value;
		ValueLabel.Text = _cue.Value.ToString("0.00", CultureInfo.InvariantCulture);
		for (var i = 0; i < _options.Count; i++)
			_options[i].IsChecked = i == _cue.SelectedOption;
		_showing = false;
	}
}
