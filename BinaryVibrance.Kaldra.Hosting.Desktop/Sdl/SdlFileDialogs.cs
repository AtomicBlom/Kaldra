using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using BinaryVibrance.Kaldra.Core.Diagnostics;
using BinaryVibrance.Kaldra.Core.Hosting;

namespace BinaryVibrance.Kaldra.Hosting.Desktop.Sdl;

/// <summary>
/// The system's file dialogs, through SDL 3 (SDL_dialog.h): the desktop host's <see cref="IHostControl.ShowFileDialog"/>.
/// SDL shows each without blocking and calls back when the user is done, on whatever thread the platform chose (on
/// Windows, a thread of the dialog's own). The answer is queued, the window woken (<c>wake</c>, so an event-driven loop
/// renders), and handed to the app on the main thread by <see cref="Update"/>, between frames.
/// </summary>
/// <remarks>
/// The dialogs aren't parented to the window: SDL parents only to a window of its own, and the host's is GLFW's.
/// </remarks>
internal sealed unsafe class SdlFileDialogs
{
	private readonly ConcurrentQueue<(Action<IReadOnlyList<string>?> Done, IReadOnlyList<string>? Paths)> _answered = new();
	private readonly Action _wake;
	private int _open;

	private SdlFileDialogs(Action wake) => _wake = wake;

	/// <summary>The dialogs, or null with the reason where SDL3.dll isn't there to show them.</summary>
	public static SdlFileDialogs? TryCreate(Action wake, out string reason) =>
		Sdl3.TryLoad(out reason) ? new SdlFileDialogs(wake) : null;

	/// <summary>Shows <paramref name="dialog"/>; <paramref name="done"/> is called by a later <see cref="Update"/>.</summary>
	public void Show(FileDialog dialog, Action<IReadOnlyList<string>?> done)
	{
		var request = new Request(this, done, dialog);
		var userdata = (void*)GCHandle.ToIntPtr(GCHandle.Alloc(request));
		Interlocked.Increment(ref _open);
		delegate* unmanaged[Cdecl]<void*, byte**, int, void> callback = &Answered;
		switch (dialog.Kind)
		{
			case FileDialogKind.OpenFile:
				Sdl3.SDL_ShowOpenFileDialog(callback, userdata, 0, request.Filters, request.FilterCount, request.Location, dialog.AllowMany ? (byte)1 : (byte)0);
				break;
			case FileDialogKind.SaveFile:
				Sdl3.SDL_ShowSaveFileDialog(callback, userdata, 0, request.Filters, request.FilterCount, request.Location);
				break;
			case FileDialogKind.OpenFolder:
				Sdl3.SDL_ShowOpenFolderDialog(callback, userdata, 0, request.Location, dialog.AllowMany ? (byte)1 : (byte)0);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(dialog), dialog.Kind, "Not a kind of file dialog.");
		}
	}

	/// <summary>
	/// Hands each answer that has come in to the app, on the main thread. While a dialog is open, SDL's events are
	/// pumped too, which some platforms' dialogs (a desktop portal on Linux) answer through.
	/// </summary>
	public void Update()
	{
		if (Volatile.Read(ref _open) > 0)
			Sdl3.SDL_PumpEvents();
		while (_answered.TryDequeue(out var answer))
			answer.Done(answer.Paths);
	}

	// SDL's callback: a list of paths ending in null, an empty one when the user cancelled, a null one when the dialog
	// failed. On any thread, and nothing may escape it into native code
	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void Answered(void* userdata, byte** list, int filter)
	{
		try
		{
			var handle = GCHandle.FromIntPtr((nint)userdata);
			var request = (Request)handle.Target!;
			handle.Free();
			List<string>? paths = null;
			if (list != null)
			{
				paths = [];
				for (var p = list; *p != null; p++)
					paths.Add(Sdl3.Text(*p));
			}
			else
				KaldraLog.Warning($"The file dialog couldn't be shown: {Sdl3.Text(Sdl3.SDL_GetError())}");
			request.Dispose();
			var owner = request.Owner;
			owner._answered.Enqueue((request.Done, paths));
			Interlocked.Decrement(ref owner._open);
			owner._wake();
		}
		catch (Exception ex)
		{
			KaldraLog.Error($"The file dialog's answer was lost: {ex}");
		}
	}

	// What SDL reads until it calls back: the filters and the default location, in native memory, freed as it answers
	private sealed class Request : IDisposable
	{
		private readonly List<nint> _strings = [];

		public Request(SdlFileDialogs owner, Action<IReadOnlyList<string>?> done, FileDialog dialog)
		{
			Owner = owner;
			Done = done;
			if (dialog.DefaultLocation is { } location)
				Location = Utf8(location);
			FilterCount = dialog.Kind == FileDialogKind.OpenFolder ? 0 : dialog.Filters.Count;
			if (FilterCount > 0)
			{
				Filters = (Sdl3.DialogFileFilter*)NativeMemory.Alloc((nuint)FilterCount, (nuint)sizeof(Sdl3.DialogFileFilter));
				for (var i = 0; i < FilterCount; i++)
					Filters[i] = new Sdl3.DialogFileFilter { Name = Utf8(dialog.Filters[i].Name), Pattern = Utf8(dialog.Filters[i].Extensions) };
			}
		}

		public SdlFileDialogs Owner { get; }
		public Action<IReadOnlyList<string>?> Done { get; }
		public byte* Location { get; }
		public Sdl3.DialogFileFilter* Filters { get; }
		public int FilterCount { get; }

		private byte* Utf8(string text)
		{
			var p = Marshal.StringToCoTaskMemUTF8(text);
			_strings.Add(p);
			return (byte*)p;
		}

		public void Dispose()
		{
			foreach (var p in _strings)
				Marshal.FreeCoTaskMem(p);
			_strings.Clear();
			if (Filters != null)
				NativeMemory.Free(Filters);
		}
	}
}
