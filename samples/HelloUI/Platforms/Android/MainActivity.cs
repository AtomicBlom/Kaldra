using Android.App;
using Android.Content.PM;

using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Hosting.Android;

namespace HelloUI;

/// <summary>The Android entry point: Android starts the app by creating this activity, which runs <see cref="HelloUIApp"/>.</summary>
[Activity(
	Label = "Kaldra Hello UI",
	MainLauncher = true,
	ConfigurationChanges = KaldraActivity.HandledConfigChanges,
	Theme = KaldraActivity.FullscreenTheme,
	ScreenOrientation = ScreenOrientation.FullUser)]
public sealed class MainActivity : KaldraActivity
{
	protected override KaldraApplication CreateApplication() => new HelloUIApp();
}
