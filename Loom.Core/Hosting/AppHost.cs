namespace Loom.Core.Hosting;

/// <summary>
/// The application-host registry — the <see cref="LoomApplication"/> counterpart of <see cref="Engine"/> (the
/// backend registry). Each referenced host assembly self-registers an <see cref="IAppHost"/> here via a module
/// initializer as it loads; <see cref="LoomApplication.Run"/> selects the registered host. Which host is
/// present is a build/RID decision (one of <c>Loom.Hosting.Desktop</c> / <c>Loom.Hosting.Web</c>), so there is
/// normally exactly one.
/// </summary>
public static class AppHost
{
	private static readonly List<IAppHost> Hosts = [];
	private static readonly Lock Gate = new();

	/// <summary>The hosts registered so far, in registration order.</summary>
	public static IReadOnlyList<IAppHost> Registered
	{
		get
		{
			lock (Gate)
				return [.. Hosts];
		}
	}

	/// <summary>
	/// Register a host so <see cref="LoomApplication.Run"/> can discover it. Called by a host's module
	/// initializer as its assembly loads. Idempotent by <see cref="IAppHost.Name"/> — a double-load can't
	/// duplicate an entry.
	/// </summary>
	public static void Register(IAppHost host)
	{
		ArgumentNullException.ThrowIfNull(host);
		lock (Gate)
		{
			foreach (var existing in Hosts)
				if (string.Equals(existing.Name, host.Name, StringComparison.OrdinalIgnoreCase))
					return;
			Hosts.Add(host);
		}
	}
}
