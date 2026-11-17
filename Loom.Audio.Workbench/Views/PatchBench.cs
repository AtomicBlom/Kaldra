using System.ComponentModel;

using SUIsei;
using SUIsei.Controls;

namespace Loom.Audio.Workbench.Views;

/// <summary>The patch bench (<c>PatchBench.xaml</c>): patches and kits to choose, a <see cref="Keyboard"/>, velocity, macros and pads, bound to <see cref="ViewModel"/>.</summary>
internal sealed partial class PatchBench : ContentControl, INotifyPropertyChanged
{
	/// <summary>A bench with nothing to show until it is given a <see cref="ViewModel"/>.</summary>
	public PatchBench() => InitializeComponent();

	/// <inheritdoc/>
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>What it shows: its bindings follow it when it is set.</summary>
	public WorkbenchViewModel? ViewModel
	{
		get;
		set
		{
			field = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ViewModel)));
		}
	}
}
