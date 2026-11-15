using System.Collections.Specialized;
using System.ComponentModel;

using SUIsei.Controls;

namespace Loom.Audio.Workbench.Views;

/// <summary>The sections (<c>SectionStrip.xaml</c>): a button each, the one playing lit; the loop.</summary>
internal sealed partial class SectionStrip : ContentControl
{
	private readonly WorkbenchViewModel _model;
	private readonly List<(SectionViewModel Section, ToggleButton Button)> _buttons = [];
	private bool _showing;

	/// <summary>A strip over <paramref name="viewModel"/>.</summary>
	public SectionStrip(WorkbenchViewModel viewModel)
	{
		_model = viewModel;
		InitializeComponent();
		Apply();
		viewModel.Sections.CollectionChanged += OnSectionsChanged;
		viewModel.PropertyChanged += OnChanged;
	}

	partial void OnComponentRebuilt() => Apply();

	private void Apply()
	{
		Heading.Foreground = Theme.Dim;
		LoopCheck.Checked += (_, _) => { if (!_showing) _model.LoopSection = true; };
		LoopCheck.Unchecked += (_, _) => { if (!_showing) _model.LoopSection = false; };
		MakeButtons();
		ShowLoop();
	}

	private void OnSectionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => MakeButtons();

	private void OnChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(WorkbenchViewModel.LoopSection))
			ShowLoop();
	}

	private void MakeButtons()
	{
		foreach (var (section, _) in _buttons)
			section.PropertyChanged -= OnSectionChanged;
		Buttons.Children.Clear();
		_buttons.Clear();
		foreach (var section in _model.Sections)
		{
			var button = Make.Toggle($"{section.Name} ({section.Bars})");
			var jumped = section;
			button.Click += (_, _) => { jumped.Jump(); ShowCurrent(); };
			section.PropertyChanged += OnSectionChanged;
			_buttons.Add((section, button));
			Buttons.Children.Add(button);
		}
		ShowCurrent();
	}

	private void OnSectionChanged(object? sender, PropertyChangedEventArgs e) => ShowCurrent();

	// The section playing lit; a click doesn't light another until the jump is heard
	private void ShowCurrent()
	{
		_showing = true;
		foreach (var (section, button) in _buttons)
			button.IsChecked = section.IsCurrent;
		_showing = false;
	}

	private void ShowLoop()
	{
		_showing = true;
		LoopCheck.IsChecked = _model.LoopSection;
		_showing = false;
	}
}
