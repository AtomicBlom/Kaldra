using Android.App;
using Android.Content.PM;

using Loom.Core.Hosting;
using Loom.Hosting.Android;

namespace HelloUI;

/// <summary>The Android entry point: Android starts the app by creating this activity, which runs <see cref="HelloUIApp"/>.</summary>
[Activity(
	Label = "Loom Hello UI",
	MainLauncher = true,
	ConfigurationChanges = LoomActivity.HandledConfigChanges,
	Theme = LoomActivity.FullscreenTheme,
	ScreenOrientation = ScreenOrientation.FullUser)]
public sealed class MainActivity : LoomActivity
{
	protected override LoomApplication CreateApplication() => new HelloUIApp();
}
