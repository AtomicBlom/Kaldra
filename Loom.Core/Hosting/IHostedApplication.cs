namespace Loom.Core.Hosting;

/// <summary>
/// The driving surface an <see cref="IAppHost"/> uses to run a <see cref="LoomApplication"/>: read its startup
/// options, hand it the live engine once the device is up, and tick it each frame. <see cref="LoomApplication"/>
/// implements this <em>explicitly</em>, so these framework-plumbing calls stay off an app author's own type —
/// the author sees only the overridable hooks and <see cref="LoomApplication.Engine"/>. A host obtains it by
/// casting the application it was handed (<c>(IHostedApplication)app</c>).
/// </summary>
public interface IHostedApplication
{
	/// <summary>Build the app's <see cref="AppOptions"/> (runs its <c>Configure</c> override). Called once before device bring-up.</summary>
	AppOptions ReadOptions();

	/// <summary>Bind the live engine and run the app's <c>LoadContent</c> (define techniques, upload resources, set the graph). Called once, after the device is up.</summary>
	void Load(IEngine engine);

	/// <summary>Advance one frame: the app's <c>Update</c>, then <c>Draw</c>, then the framework's own unconditional <see cref="IEngine.Render"/>.</summary>
	void Tick(AppTime time);
}
