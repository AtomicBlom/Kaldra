using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;

namespace BinaryVibrance.Kaldra.Graph;

/// <summary>
/// Every technique defined on an engine, by name. Backend-neutral and device-free, so the record-time checks that
/// consult it are unit-testable without a GPU, and so a technique-name miss reports the same way on every backend
/// rather than once per backend's own exception type.
/// <para>
/// This does not replace a backend's pipeline cache. Those are keyed by baked content (attachment formats, blend
/// state) and stay where they are; this is the one place a <em>name</em> is resolved.
/// </para>
/// </summary>
public sealed class TechniqueRegistry
{
	private readonly Dictionary<string, TechniqueRecord> _techniques = [];

	/// <summary>Record a technique under <paramref name="name"/>, replacing any previous registration (a hot reload replays this).</summary>
	public void Register(string name, TechniqueDescriptor descriptor, TechniqueShape shape) =>
		_techniques[name] = new TechniqueRecord(name, descriptor, shape);

	/// <summary>The technique registered under <paramref name="name"/>, or null when there is none.</summary>
	public TechniqueRecord? Find(string name) => _techniques.GetValueOrDefault(name);

	/// <summary>
	/// The technique registered under <paramref name="name"/>. Throws <see cref="TechniqueNotDefinedException"/> when
	/// there is none — the single origin of that error, so a typo'd name reads identically on every backend.
	/// </summary>
	public TechniqueRecord Require(string name) =>
		_techniques.TryGetValue(name, out var record) ? record : throw new TechniqueNotDefinedException(name);
}
