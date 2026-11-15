using System.Collections.Specialized;
using System.ComponentModel;

using SUIsei;
using SUIsei.Controls;

namespace Loom.Audio.Workbench.Views;

/// <summary>The patch bench (<c>PatchBench.xaml</c>): patches and kits to choose, a <see cref="Keyboard"/>, velocity, macros and pads.</summary>
internal sealed partial class PatchBench : ContentControl
{
	private readonly WorkbenchViewModel _model;
	private readonly List<ToggleButton> _patches = [];
	private readonly List<ToggleButton> _kits = [];
	private Keyboard? _keyboard;
	private bool _showing;

	/// <summary>A bench over <paramref name="viewModel"/>.</summary>
	public PatchBench(WorkbenchViewModel viewModel)
	{
		_model = viewModel;
		InitializeComponent();
		Apply();
		viewModel.PropertyChanged += OnChanged;
		viewModel.Pads.CollectionChanged += OnPadsChanged;
	}

	partial void OnComponentRebuilt() => Apply();

	private void Apply()
	{
		Heading.Foreground = Theme.Dim;
		VelocityLabel.Foreground = Theme.Dim;
		OctaveLabel.Foreground = Theme.Dim;
		PatchButtons.Children.Clear();
		_patches.Clear();
		for (var i = 0; i < _model.Patches.Count; i++)
		{
			var index = i;
			var button = Make.Toggle(_model.Patches[i]);
			button.Click += (_, _) => { _model.SelectedPatch = index; Show(); };
			_patches.Add(button);
			PatchButtons.Children.Add(button);
		}
		KitButtons.Children.Clear();
		_kits.Clear();
		for (var i = 0; i < _model.Kits.Count; i++)
		{
			var index = i;
			var button = Make.Toggle($"kit: {_model.Kits[i]}");
			button.Click += (_, _) => { _model.SelectedKit = index; Show(); };
			_kits.Add(button);
			KitButtons.Children.Add(button);
		}
		_keyboard = new Keyboard(_model);
		KeyboardHost.Content = _keyboard;
		VelocitySlider.ValueChanged += (_, _) => { if (!_showing) _model.Velocity = (float)VelocitySlider.Value; };
		MacroSliders.Children.Clear();
		for (var m = 0; m < _model.Macros.Count; m++)
		{
			var macro = m;
			var row = new StackPanel { Orientation = Orientation.Horizontal };
			row.Children.Add(new TextBlock { Text = $"macro {m}", FontSize = 12f, Width = 70f, Foreground = Theme.Dim, VerticalAlignment = VerticalAlignment.Center });
			var slider = new Slider { Minimum = 0, Maximum = 1, StepFrequency = 0.01f, Width = 220f, Height = 26f, Value = _model.Macros[m] };
			slider.ValueChanged += (_, _) => _model.SetMacro(macro, (float)slider.Value);
			row.Children.Add(slider);
			MacroSliders.Children.Add(row);
		}
		MakePads();
		Show();
	}

	private void OnPadsChanged(object? sender, NotifyCollectionChangedEventArgs e) => MakePads();

	private void MakePads()
	{
		PadButtons.Children.Clear();
		for (var i = 0; i < _model.Pads.Count; i++)
		{
			var index = i;
			var pad = Make.Button(_model.Pads[i]);
			pad.Click += (_, _) => _model.HitPad(index);
			PadButtons.Children.Add(pad);
		}
	}

	private void OnChanged(object? sender, PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
			case nameof(WorkbenchViewModel.SelectedPatch) or nameof(WorkbenchViewModel.SelectedKit) or nameof(WorkbenchViewModel.Velocity):
				Show();
				break;
			case nameof(WorkbenchViewModel.Octave):
				Show();
				_keyboard?.Relabel();
				break;
		}
	}

	private void Show()
	{
		_showing = true;
		for (var i = 0; i < _patches.Count; i++)
			_patches[i].IsChecked = i == _model.SelectedPatch;
		for (var i = 0; i < _kits.Count; i++)
			_kits[i].IsChecked = i == _model.SelectedKit;
		VelocitySlider.Value = _model.Velocity;
		OctaveLabel.Text = $"octave {_model.Octave} (Z and X move it)";
		MacroSliders.Visibility = _model.SelectedPatch >= 0 ? Visibility.Visible : Visibility.Collapsed;
		_showing = false;
	}
}
