using Android.App;
using Android.Content;

using Loom.Core.Hosting;

namespace Loom.Hosting.Android;

/// <summary>
/// The app's control channel back to the Android host: exit finishes the activity, the display scale is the screen's
/// density, and the clipboard is the system's. Every call comes on the UI thread, where the app's frames run.
/// </summary>
internal sealed class AndroidHostControl(LoomActivity activity) : IHostControl
{
	public void RequestExit() => activity.Finish();

	// Read from a field the activity refreshes on a configuration change: asking Android's resources for it would make
	// Java objects on every call, and an app reads it every frame.
	public float DisplayScale => activity.CurrentDisplayScale;

	public string? GetClipboardText()
	{
		var clipboard = (ClipboardManager?)activity.GetSystemService(Context.ClipboardService);
		var clip = clipboard?.PrimaryClip;
		if (clip is null || clip.ItemCount == 0)
			return null;
		return clip.GetItemAt(0)?.CoerceToText(activity);
	}

	public bool SetClipboardText(string text)
	{
		if (activity.GetSystemService(Context.ClipboardService) is not ClipboardManager clipboard)
			return false;
		clipboard.PrimaryClip = ClipData.NewPlainText("text", text);
		return true;
	}

	// The APK's assets, where the Loom SDK packs an app's LoomContent. Android's asset manager may be read from any thread.
	public Stream OpenContent(string path)
	{
		try
		{
			return activity.Assets!.Open(path);
		}
		catch (Java.IO.FileNotFoundException ex)
		{
			throw new FileNotFoundException($"The app has no content at '{path}' (looked in the APK's assets).", path, ex);
		}
	}
}
