using System.Runtime.CompilerServices;

using Loom.Core.Hosting;

namespace Loom.Hosting.Android;

/// <summary>
/// The Android <see cref="IAppHost"/>. An Android app has no <c>Main</c> to block in: Android starts it by creating its
/// <see cref="LoomActivity"/>, which calls <see cref="LoomApplication.Run"/> from <c>OnCreate</c>. So, like the web host,
/// <see cref="Run"/> only lets the application be published as <see cref="LoomApplication.Current"/> and returns; the
/// activity brings the device up when its surface exists and drives the frames from then on.
/// </summary>
public sealed class AndroidAppHost : IAppHost
{
#pragma warning disable CA2255 // ModuleInitializer is the intended self-registration hook, matching the backends.
	[ModuleInitializer]
	internal static void Register() => AppHost.Register(new AndroidAppHost());
#pragma warning restore CA2255

	public string Name => "Android";

	public void Run(LoomApplication app, string[] args)
	{
		// Nothing to do: the activity that called Run owns the surface, the engine and the frame loop.
	}
}
