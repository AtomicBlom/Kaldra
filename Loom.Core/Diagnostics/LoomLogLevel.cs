namespace Loom.Core.Diagnostics;

/// <summary>How much attention a <see cref="LoomLog"/> message wants.</summary>
public enum LoomLogLevel
{
	/// <summary>Routine progress — which device was chosen, that a shader reloaded.</summary>
	Info,

	/// <summary>Suspicious but survivable: a pass whose output nothing consumes, a requested feature that was unavailable.</summary>
	Warning,

	/// <summary>Something failed, but not so badly that the frame had to stop. Anything that must stop it throws.</summary>
	Error,
}
