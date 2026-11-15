using System.ComponentModel;

using Loom.Audio.Synth.Music;

using SUIsei;
using SUIsei.Controls;

namespace Loom.Audio.Workbench.Views;

/// <summary>A part (<c>PlacementRow.xaml</c>): lit while it plays; auto, on or off; a click shows its pattern.</summary>
internal sealed partial class PlacementRow : ContentControl
{
	private readonly PlacementViewModel _placement;
	private bool _showing;

	/// <summary>A row for <paramref name="placement"/>.</summary>
	public PlacementRow(PlacementViewModel placement)
	{
		_placement = placement;
		InitializeComponent();
		Apply();
		placement.PropertyChanged += OnChanged;
	}

	partial void OnComponentRebuilt() => Apply();

	private void Apply()
	{
		NameLabel.Text = _placement.Name;
		NameLabel.Foreground = Theme.Ink;
		ConditionLabel.Text = _placement.ConditionText;
		ConditionLabel.Foreground = Theme.Dim;
		ConditionLabel.Visibility = _placement.ConditionText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		Row.Background = Theme.Panel;
		Row.PointerPressed += (_, _) => _placement.Select();
		AutoButton.Click += (_, _) => Choose(PlacementState.Auto);
		OnButton.Click += (_, _) => Choose(PlacementState.On);
		OffButton.Click += (_, _) => Choose(PlacementState.Off);
		Show();
	}

	private void Choose(PlacementState state)
	{
		if (!_showing)
			_placement.State = state;
		Show();
	}

	private void OnChanged(object? sender, PropertyChangedEventArgs e) => Show();

	private void Show()
	{
		_showing = true;
		AutoButton.IsChecked = _placement.State == PlacementState.Auto;
		OnButton.IsChecked = _placement.State == PlacementState.On;
		OffButton.IsChecked = _placement.State == PlacementState.Off;
		Dot.Fill = _placement.IsActive ? Theme.Accent : Theme.Off;
		_showing = false;
	}
}
