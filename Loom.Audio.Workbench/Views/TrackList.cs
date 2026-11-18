using System.ComponentModel;

using SUIsei.Controls;

namespace Loom.Audio.Workbench.Views;

/// <summary>The tracks (<c>TrackList.xaml</c>): a row each with its parts under it, bound to <see cref="ViewModel"/>.</summary>
internal sealed partial class TrackList : ContentControl, INotifyPropertyChanged
{
	/// <summary>A list with nothing to show until it is given a <see cref="ViewModel"/>.</summary>
	public TrackList() => InitializeComponent();

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
