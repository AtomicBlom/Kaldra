using SUIsei;
using SUIsei.Controls;

namespace Loom.Audio.Workbench.Views;

/// <summary>Controls the views make in code, styled alike.</summary>
internal static class Make
{
	public static TextBlock Text(string text, float size = 14f, bool dim = false) =>
		new() { Text = text, FontSize = size, Foreground = dim ? Theme.Dim : Theme.Ink, VerticalAlignment = VerticalAlignment.Center };

	// A button's label, its margin standing in for the padding a button hasn't
	private static TextBlock Label(string text) =>
		new() { Text = text, FontSize = 13f, Margin = new Thickness(8, 3, 8, 4), HorizontalAlignment = HorizontalAlignment.Center };

	public static ToggleButton Toggle(string text, string? name = null, float width = float.NaN)
	{
		var button = new ToggleButton { Content = Label(text), Margin = new Thickness(0, 0, 4, 4) };
		if (!float.IsNaN(width))
			button.Width = width;
		if (name is not null)
			button.Name = name;
		return button;
	}

	public static Button Button(string text, string? name = null)
	{
		var button = new Button { Content = Label(text), Margin = new Thickness(0, 0, 4, 4) };
		if (name is not null)
			button.Name = name;
		return button;
	}
}
