namespace Loom.Core.Hosting;

/// <summary>
/// The backend registry and the host's entry point to rendering. Each referenced backend
/// assembly self-registers an <see cref="IRenderBackend"/> here (via a module initializer) as it loads, so the
/// host discovers what is available at runtime and selects one by name:
/// <code>
/// var backend = Engine.AvailableEngines.FirstOrDefault(e =&gt; e.Name == settings.PreferredBackend)
///               ?? Engine.AvailableEngines.First();
/// using var engine = backend.Create(window.VkSurface, width, height, settings);
/// </code>
/// Which backends are <em>present</em> is decided by the build (the <c>&lt;UseVulkan&gt;</c>/
/// <c>&lt;UseDirectX12&gt;</c> flags pull in the assemblies); which present backend <em>runs</em> is the runtime
/// selection above. A backend's module initializer fires when its assembly is first touched — constructing the
/// backend's <see cref="EngineSettings"/> subclass (the one sanctioned place a backend type appears in app
/// code) is enough to load it before <see cref="AvailableEngines"/> is read.
/// </summary>
public static class Engine
{
	private static readonly List<IRenderBackend> Backends = [];
	private static readonly Lock Gate = new();

	/// <summary>The backends registered so far, in registration order. See the type remarks for the selection idiom.</summary>
	public static IReadOnlyList<IRenderBackend> AvailableEngines
	{
		get
		{
			lock (Gate)
				return [.. Backends];
		}
	}

	/// <summary>
	/// Register a backend so the host can discover it via <see cref="AvailableEngines"/>. Called by a backend's
	/// module initializer as its assembly loads. Idempotent by <see cref="IRenderBackend.Name"/> — registering a
	/// second backend with a name already present is ignored, so a double-load can't duplicate an entry.
	/// </summary>
	public static void Register(IRenderBackend backend)
	{
		ArgumentNullException.ThrowIfNull(backend);
		lock (Gate)
		{
			foreach (var existing in Backends)
				if (string.Equals(existing.Name, backend.Name, StringComparison.OrdinalIgnoreCase))
					return;
			Backends.Add(backend);
		}
	}
}
