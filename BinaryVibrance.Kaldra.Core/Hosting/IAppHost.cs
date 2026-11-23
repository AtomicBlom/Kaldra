namespace BinaryVibrance.Kaldra.Core.Hosting;

/// <summary>
/// A platform host that runs a <see cref="KaldraApplication"/> — the desktop host owns a window + a blocking
/// frame loop; the browser host owns a canvas + a requestAnimationFrame loop. Each host assembly self-registers
/// an instance into <see cref="AppHost"/> via a module initializer as it loads, exactly as an
/// <see cref="IRenderBackend"/> registers into <see cref="Backends"/>. Which host is <em>present</em> is decided
/// by the build (the RID pulls in <c>BinaryVibrance.Kaldra.Hosting.Desktop</c> or <c>BinaryVibrance.Kaldra.Hosting.Web</c>); <see cref="KaldraApplication.Run"/>
/// dispatches to it.
/// </summary>
public interface IAppHost
{
	/// <summary>A stable name for logs/diagnostics (e.g. <c>"Desktop"</c>, <c>"Web"</c>).</summary>
	string Name { get; }

	/// <summary>
	/// Run <paramref name="app"/> to completion. The desktop host creates the window + engine and blocks in its
	/// frame loop until the window closes. The web host registers the app and returns promptly — device bring-up
	/// and the frame loop are driven later by the JS side (which supplies the canvas), since a browser app cannot
	/// block its entry thread. Either way the host drives the app through its <see cref="IHostedApplication"/>
	/// surface (<c>(IHostedApplication)app</c>).
	/// </summary>
	void Run(KaldraApplication app, string[] args);
}
