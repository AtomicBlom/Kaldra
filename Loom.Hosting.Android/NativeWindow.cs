using System.Runtime.InteropServices;

using Android.Runtime;
using Android.Views;

namespace Loom.Hosting.Android;

/// <summary>
/// The NDK's <c>ANativeWindow</c> behind a Java <see cref="Surface"/>, which is what <c>vkCreateAndroidSurfaceKHR</c>
/// takes. <see cref="FromSurface"/> returns a window with a reference the caller owns; <see cref="Release"/> drops it.
/// </summary>
internal static partial class NativeWindow
{
	/// <summary>The native window behind <paramref name="surface"/>, with a reference the caller must <see cref="Release"/>. Call it on a thread attached to the JVM.</summary>
	public static nint FromSurface(Surface surface)
	{
		var window = ANativeWindow_fromSurface(JNIEnv.Handle, surface.Handle);
		if (window == 0)
			throw new InvalidOperationException("ANativeWindow_fromSurface returned no window: the surface is not valid (was it already destroyed?).");
		return window;
	}

	/// <summary>Drop the reference <see cref="FromSurface"/> took.</summary>
	public static void Release(nint window)
	{
		if (window != 0)
			ANativeWindow_release(window);
	}

	[LibraryImport("android")]
	private static partial nint ANativeWindow_fromSurface(nint env, nint surface);

	[LibraryImport("android")]
	private static partial void ANativeWindow_release(nint window);
}
