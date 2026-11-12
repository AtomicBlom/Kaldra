using System.Numerics;
using System.Runtime.InteropServices;

using Loom.Input;

namespace Loom.Hosting.Desktop;

/// <summary>
/// A touch screen on Windows, which GLFW doesn't read: the window's procedure is subclassed for the pointer messages
/// (<c>WM_POINTERDOWN</c>, <c>WM_POINTERUPDATE</c>, <c>WM_POINTERUP</c>, Windows 8 and later), and each finger is fed to
/// the sink as its own <see cref="InputEventKind.Touch"/> contact, in client pixels as the mouse is. A touch handled
/// here is never handed on, so Windows doesn't also make it a mouse (the first finger as a click, a press and hold as a
/// right-click, a lag while it decides); a pen and the mouse go on to GLFW as before. Its visual feedback (the circles
/// under a finger, the press-and-hold ring) is turned off for the window, as a game has its own.
/// </summary>
/// <remarks>
/// One window per process: the procedure is static, as a native callback must be. Messages arrive on the thread that
/// pumps the window's events, which is the thread Silk's own input fires on, so the sink is fed from the one thread.
/// </remarks>
internal sealed unsafe partial class Win32TouchPump : IDisposable
{
	private const uint WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247, WM_POINTERCAPTURECHANGED = 0x024C;
	private const uint PT_TOUCH = 2;
	private const uint POINTER_FLAG_CANCELED = 0x8000;
	private const int GWLP_WNDPROC = -4;

	// FEEDBACK_TYPE: touch's contact circles, tap, double tap, press and hold, right tap, and press and tap
	private static readonly uint[] FeedbackOff = [1, 7, 8, 9, 10, 11];

	private static Win32TouchPump? s_current;
	private static nint s_previous;

	private readonly nint _hwnd;
	private readonly InputSink _sink;
	private readonly Dictionary<int, Vector2> _held = [];
	private bool _disposed;

	private Win32TouchPump(nint hwnd, InputSink sink)
	{
		_hwnd = hwnd;
		_sink = sink;
		foreach (var type in FeedbackOff)
		{
			int off = 0;
			SetWindowFeedbackSetting(hwnd, type, 0, sizeof(int), &off);
		}
		s_current = this;
		s_previous = SetWindowLongPtrW(hwnd, GWLP_WNDPROC, (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&WndProc);
	}

	/// <summary>Subclasses <paramref name="hwnd"/> for touch, or says <paramref name="why"/> not: not Windows 8 or later, a
	/// 32-bit process, no window, or a window already taken.</summary>
	public static Win32TouchPump? TryCreate(nint hwnd, InputSink sink, out string? why)
	{
		ArgumentNullException.ThrowIfNull(sink);
		why = !OperatingSystem.IsWindowsVersionAtLeast(6, 2) ? "not Windows 8 or later"
			: IntPtr.Size != 8 ? "a 32-bit process"
			: hwnd == 0 ? "no Win32 window"
			: s_current is not null ? "another window already has it"
			: null;
		return why is null ? new Win32TouchPump(hwnd, sink) : null;
	}

	/// <summary>Cancels every finger held, as the window loses focus and Windows stops telling it about them.</summary>
	public void ReleaseAllHeld()
	{
		foreach (var (id, at) in _held)
			_sink(InputEvent.Touch(TouchPhase.Canceled, id, at.X, at.Y));
		_held.Clear();
	}

	/// <summary>Puts the window's own procedure back.</summary>
	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		SetWindowLongPtrW(_hwnd, GWLP_WNDPROC, s_previous);
		s_current = null;
	}

	[UnmanagedCallersOnly]
	private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
	{
		if (msg is WM_POINTERDOWN or WM_POINTERUPDATE or WM_POINTERUP or WM_POINTERCAPTURECHANGED
			&& s_current is { } self && hwnd == self._hwnd && self.OnPointer(msg, wParam, lParam))
			return 0;
		return CallWindowProcW(s_previous, hwnd, msg, wParam, lParam);
	}

	// A pointer message, and true if it was a touch, handled
	private bool OnPointer(uint msg, nuint wParam, nint lParam)
	{
		var id = (int)(wParam & 0xFFFF);
		if (msg == WM_POINTERCAPTURECHANGED)
		{
			// The window lost the contact (another took it): gone, not lifted
			if (!_held.Remove(id, out var last))
				return false;
			_sink(InputEvent.Touch(TouchPhase.Canceled, id, last.X, last.Y));
			return true;
		}

		if (GetPointerType((uint)id, out var type) == 0 || type != PT_TOUCH)
			return false;
		var point = new Point { X = (short)(lParam & 0xFFFF), Y = (short)((lParam >> 16) & 0xFFFF) };
		ScreenToClient(_hwnd, ref point);
		var at = new Vector2(point.X, point.Y);
		switch (msg)
		{
			case WM_POINTERDOWN:
				_held[id] = at;
				_sink(InputEvent.Touch(TouchPhase.Began, id, at.X, at.Y));
				break;
			case WM_POINTERUPDATE:
				if (_held.TryGetValue(id, out var was) && was == at)
					break;
				_held[id] = at;
				_sink(InputEvent.Touch(TouchPhase.Moved, id, at.X, at.Y));
				break;
			default:
				var canceled = ((uint)(wParam >> 16) & POINTER_FLAG_CANCELED) != 0;
				_held.Remove(id);
				_sink(InputEvent.Touch(canceled ? TouchPhase.Canceled : TouchPhase.Ended, id, at.X, at.Y));
				break;
		}
		return true;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Point
	{
		public int X, Y;
	}

	[LibraryImport("user32.dll")]
	private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);

	[LibraryImport("user32.dll")]
	private static partial nint CallWindowProcW(nint previous, nint hwnd, uint msg, nuint wParam, nint lParam);

	[LibraryImport("user32.dll")]
	private static partial int GetPointerType(uint pointerId, out uint type);

	[LibraryImport("user32.dll")]
	private static partial int ScreenToClient(nint hwnd, ref Point point);

	[LibraryImport("user32.dll")]
	private static partial int SetWindowFeedbackSetting(nint hwnd, uint feedback, uint flags, uint size, void* configuration);
}
