using System.Collections.Specialized;

using SUIsei.Controls;

namespace Loom.Audio.Workbench.Views;

/// <summary>The tracks (<c>TrackList.xaml</c>): a <see cref="TrackRow"/> each.</summary>
internal sealed partial class TrackList : ContentControl
{
	private readonly WorkbenchViewModel _model;

	/// <summary>A list over <paramref name="viewModel"/>'s tracks.</summary>
	public TrackList(WorkbenchViewModel viewModel)
	{
		_model = viewModel;
		InitializeComponent();
		Apply();
		viewModel.Tracks.CollectionChanged += OnTracksChanged;
	}

	partial void OnComponentRebuilt() => Apply();

	private void OnTracksChanged(object? sender, NotifyCollectionChangedEventArgs e) => MakeRows();

	private void Apply()
	{
		Heading.Foreground = Theme.Dim;
		MakeRows();
	}

	private void MakeRows()
	{
		Rows.Children.Clear();
		foreach (var track in _model.Tracks)
			Rows.Children.Add(new TrackRow(track));
	}
}
