using SUIsei;
using SUIsei.Controls;

namespace Loom.Audio.Workbench.Views;

/// <summary>One pass of a part drawn small on its clip: its notes as bars by row, over the clip's width.</summary>
internal sealed class ClipPattern : ContentControl
{
	private const float DrawnWidth = 196f;
	private const float DrawnHeight = 22f;

	private readonly Canvas _notes = new() { Width = DrawnWidth, Height = DrawnHeight };

	public ClipPattern()
	{
		IsTabStop = false;
		IsHitTestVisible = false;
		Content = _notes;
	}

	/// <summary>The part's clip, whose notes it draws.</summary>
	public PlacementViewModel? Part
	{
		get;
		set
		{
			field = value;
			Draw();
		}
	}

	private void Draw()
	{
		_notes.Children.Clear();
		if (Part is not { LoopTicks: > 0, Rows: > 0 } clip)
			return;
		var rowHeight = DrawnHeight / clip.Rows;
		var scale = DrawnWidth / clip.LoopTicks;
		var brush = clip.KindText.StartsWith("hits", StringComparison.Ordinal) ? Theme.Accent : Theme.Note;
		foreach (var note in clip.Notes)
		{
			var bar = new Border
			{
				Width = MathF.Max(1.5f, note.Length * scale - 0.5f),
				Height = MathF.Max(1.5f, rowHeight - 1f),
				Background = brush,
				Opacity = 0.35f + 0.65f * note.Velocity,
				CornerRadius = new CornerRadius(1),
			};
			Canvas.SetLeft(bar, note.Tick * scale);
			Canvas.SetTop(bar, note.Row * rowHeight);
			_notes.Children.Add(bar);
		}
	}
}
