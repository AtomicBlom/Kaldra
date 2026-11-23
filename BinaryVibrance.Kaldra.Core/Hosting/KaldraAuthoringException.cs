namespace BinaryVibrance.Kaldra.Core.Hosting;

/// <summary>
/// The base of every exception Kaldra raises for an <em>authoring</em> mistake — a graph, technique, material or draw
/// that contradicts itself or the shader it names. Distinct from a backend's own exception type, which reports a
/// genuine <em>device</em> failure (a lost adapter, an out-of-memory allocation, an unsupported format).
/// <para>
/// The distinction is what makes the error handleable: the same mistake raises the same type with the same message on
/// every backend, so an app can <c>catch (KaldraAuthoringException)</c> once instead of learning one spelling per
/// backend. A backend that invented its own type for a user error would break that, and a fourth backend would mint a
/// fourth spelling.
/// </para>
/// </summary>
public abstract class KaldraAuthoringException(string message) : Exception(message);
