using System.Collections.Concurrent;

using Loom.Core.Diagnostics;
using Loom.Core.Materials;

namespace Loom.Graph;

/// <summary>
/// Tracks which techniques each shader backs, for hot-reload. A backend records every technique it defines here;
/// when a watched shader artifact changes, the backend maps it to a logical shader name and queues it, and draining
/// (on the render thread) replays each affected technique's descriptor through a backend-supplied redefine callback,
/// bracketed by a GPU drain and a pipeline-cache reset. Pure bookkeeping over neutral descriptors — the file
/// watching, the artifact-name parsing, and the GPU/cache work stay in the backend, behind the callbacks.
/// </summary>
public sealed class ShaderReloadRegistry
{
	private readonly Dictionary<string, List<string>> _techniquesByShader = [];
	private readonly Dictionary<string, TechniqueDescriptor> _descriptorsByName = [];
	private readonly ConcurrentQueue<string> _reloadQueue = new();

	/// <summary>
	/// Remember that technique <paramref name="name"/> is backed by <paramref name="descriptor"/>'s shader (one
	/// shader can back many techniques, e.g. a bloom up/down sample), and stash the descriptor so a later reload can
	/// replay it. Idempotent per name; call it from the backend's DefineTechnique.
	/// </summary>
	public void Record(string name, TechniqueDescriptor descriptor)
	{
		_descriptorsByName[name] = descriptor;
		if (!_techniquesByShader.TryGetValue(descriptor.Shader, out var users))
			_techniquesByShader[descriptor.Shader] = users = [];
		if (!users.Contains(name))
			users.Add(name);
	}

	/// <summary>
	/// Queue logical shader <paramref name="shader"/> for reload if any technique uses it. The backend calls this
	/// from its file-watcher callback after parsing the artifact filename back to the shader name. Safe to call from
	/// the watcher thread — it only enqueues; the reload itself happens on the render thread in <see cref="Drain"/>.
	/// </summary>
	public void QueueIfTracked(string shader)
	{
		if (_techniquesByShader.ContainsKey(shader))
			_reloadQueue.Enqueue(shader);
	}

	/// <summary>
	/// Replay every queued shader's techniques through <paramref name="redefine"/> (the backend's DefineTechnique),
	/// after <paramref name="waitGpuIdle"/> drains in-flight work and before <paramref name="resetCaches"/> evicts
	/// the stale baked pipelines — the next frame re-bakes from the new bytecode. A no-op when nothing is queued.
	/// Call on the render thread, before recording a frame.
	/// </summary>
	public void Drain(Action waitGpuIdle, Action<string, TechniqueDescriptor> redefine, Action resetCaches)
	{
		if (_reloadQueue.IsEmpty)
			return;

		var shaders = new HashSet<string>();
		while (_reloadQueue.TryDequeue(out var shader))
			shaders.Add(shader);
		if (shaders.Count == 0)
			return;

		waitGpuIdle();
		foreach (var shader in shaders)
		{
			if (!_techniquesByShader.TryGetValue(shader, out var techniques))
				continue;
			foreach (var name in techniques)
				redefine(name, _descriptorsByName[name]);
			LoomLog.Info($"reloaded shader '{shader}' → {string.Join(", ", techniques)}");
		}
		resetCaches();
	}
}
