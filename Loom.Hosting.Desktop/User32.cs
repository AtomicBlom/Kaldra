using System.Runtime.InteropServices;

namespace Loom.Hosting.Desktop;

/// <summary>The user32 calls the desktop host's display queries share. Windows only: callers check first.</summary>
internal static partial class User32
{
	/// <summary><c>MONITOR_DEFAULTTONEAREST</c>: the monitor nearest a window that is on none.</summary>
	public const uint MonitorDefaultToNearest = 2;

	/// <summary>The monitor a window is on, or most of it.</summary>
	[LibraryImport("user32.dll")]
	public static partial nint MonitorFromWindow(nint hwnd, uint flags);
}
