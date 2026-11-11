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

	/// <summary>The DPI of the monitor a window is on (96 at 100%), as a per-monitor-aware process sees it; 0 for an invalid window.</summary>
	[LibraryImport("user32.dll")]
	public static partial uint GetDpiForWindow(nint hwnd);
}
