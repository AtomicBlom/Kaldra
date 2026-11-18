using System.ComponentModel;

using SUIsei.Controls;

namespace Loom.Audio.Workbench.Views;

/// <summary>The sections (<c>SectionStrip.xaml</c>): a button each, the one playing lit; the loop; bound to <see cref="ViewModel"/>.</summary>
internal sealed partial class SectionStrip : ContentControl, INotifyPropertyChanged
{
	/// <summary>A strip with nothing to show until it is given a <see cref="ViewModel"/>.</summary>
	public SectionStrip() => InitializeComponent();

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
