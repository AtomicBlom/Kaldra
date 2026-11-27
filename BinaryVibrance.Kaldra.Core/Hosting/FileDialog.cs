namespace BinaryVibrance.Kaldra.Core.Hosting;

/// <summary>Which of the system's file dialogs to show (see <see cref="FileDialog"/>).</summary>
public enum FileDialogKind
{
	/// <summary>Choose one existing file (or several, with <see cref="FileDialog.AllowMany"/>).</summary>
	OpenFile,

	/// <summary>Choose one existing folder (or several, with <see cref="FileDialog.AllowMany"/>).</summary>
	OpenFolder,

	/// <summary>Choose where to write a file, which may not exist yet. The system asks before replacing one that does.</summary>
	SaveFile,
}

/// <summary>
/// One entry in a file dialog's list of kinds of file: a name the user reads and the extensions it covers, without dots,
/// joined by <c>;</c> (<c>"lunelle;zip"</c>), or <c>"*"</c> for every file.
/// </summary>
public readonly record struct FileDialogFilter(string Name, string Extensions);

/// <summary>
/// A request for one of the system's file dialogs, shown with <see cref="IHostControl.ShowFileDialog"/> (an app calls
/// <see cref="KaldraApplication.ShowFileDialogAsync"/>).
/// </summary>
public sealed record FileDialog
{
	/// <summary>Which dialog: open a file, open a folder, or save a file.</summary>
	public FileDialogKind Kind { get; init; }

	/// <summary>The kinds of file offered, the first chosen at the start. None offers every file. Folders take none.</summary>
	public IReadOnlyList<FileDialogFilter> Filters { get; init; } = [];

	/// <summary>The folder to start in, or a file to start at (a save's suggested name); null for the system's choice.</summary>
	public string? DefaultLocation { get; init; }

	/// <summary>Whether more than one file or folder may be chosen. A save chooses one.</summary>
	public bool AllowMany { get; init; }
}
