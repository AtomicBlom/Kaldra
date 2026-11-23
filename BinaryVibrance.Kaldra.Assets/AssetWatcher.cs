namespace BinaryVibrance.Kaldra.Assets;

/// <summary>
/// Watches an <see cref="IAssetSource"/>'s roots for changed files (a given set of extensions) and raises
/// <see cref="Changed"/> — once per coalesced burst — with the changed file's <em>logical leaf name</em>
/// (e.g. <c>"bloom_sample.spv"</c>). The consumer re-resolves through the source, so layering/priority is honoured.
/// Carries no graphics-API types: it reports "logical asset X changed", nothing more.
/// <para>
/// <b>Debounced.</b> Editors and compilers write files in bursts (multiple events per save; slangc rewrites
/// <c>.spv</c>+<c>.json</c>(+<c>.dxil</c>) together). A single resettable timer coalesces rapid changes — the event
/// fires only once the burst goes quiet for <c>debounceMs</c>. The timer callback runs on a thread-pool thread, so
/// the consumer must marshal any non-thread-safe work (e.g. GPU calls) to its own thread.
/// </para>
/// </summary>
public sealed class AssetWatcher : IDisposable
{
	private readonly List<FileSystemWatcher> _watchers = [];
	private readonly System.Threading.Timer _debounce;
	private readonly int _debounceMs;
	private readonly HashSet<string> _extensions;
	private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
	private readonly Lock _gate = new();
	private bool _disposed;

	/// <summary>Raised after the debounce window with the logical leaf name of each distinct changed file.</summary>
	public event Action<string>? Changed;

	/// <summary>Watch every root of <paramref name="source"/> (recursively) for files whose extension is in
	/// <paramref name="extensions"/> (e.g. <c>".spv"</c>, <c>".json"</c>, <c>".dxil"</c>), debounced by
	/// <paramref name="debounceMs"/>. Missing root directories are skipped.</summary>
	public AssetWatcher(IAssetSource source, IEnumerable<string> extensions, int debounceMs = 150)
	{
		ArgumentNullException.ThrowIfNull(source);
		_debounceMs = debounceMs;
		_extensions = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
		_debounce = new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

		foreach (var root in source.Roots)
		{
			if (!Directory.Exists(root))
				continue;
			var watcher = new FileSystemWatcher(root)
			{
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
			};
			watcher.Changed += OnFileEvent;
			watcher.Created += OnFileEvent;
			watcher.Renamed += OnFileEvent; // editors that write-temp-then-rename still trigger
			watcher.EnableRaisingEvents = true;
			_watchers.Add(watcher);
		}
	}

	private void OnFileEvent(object sender, FileSystemEventArgs e)
	{
		if (string.IsNullOrEmpty(e.Name))
			return;
		// Path.GetExtension(".vs.dxil") == ".dxil", so a single ".dxil" entry covers every per-stage file.
		if (!_extensions.Contains(Path.GetExtension(e.Name)))
			return;
		lock (_gate)
			_pending.Add(Path.GetFileName(e.Name));
		_debounce.Change(_debounceMs, Timeout.Infinite); // trailing-edge: reset on every event, fire once quiet
	}

	private void Flush()
	{
		string[] batch;
		lock (_gate)
		{
			if (_disposed || _pending.Count == 0)
				return;
			batch = [.. _pending];
			_pending.Clear();
		}
		foreach (var leaf in batch)
			Changed?.Invoke(leaf);
	}

	public void Dispose()
	{
		// Mark disposed under the gate first so a debounce callback already queued sees it and bails in Flush,
		// rather than raising Changed against state the consumer has begun tearing down.
		lock (_gate)
			_disposed = true;
		foreach (var watcher in _watchers)
			watcher.Dispose();
		_debounce.Dispose();
	}
}
