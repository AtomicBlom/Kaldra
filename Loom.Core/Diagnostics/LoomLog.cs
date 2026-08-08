namespace Loom.Core.Diagnostics;

/// <summary>
/// Where the engine's diagnostics go. The engine writes through this rather than to the console directly, because a
/// windowed app has no console to read: a <c>WinExe</c> detaches from its parent terminal, so a warning written to
/// stdout is not merely easy to miss, it is invisible. An app that wants to see them points <see cref="Sink"/> at its
/// own logger, overlay, or test buffer.
/// <para>
/// Static and mutable by design. These are engine-wide diagnostics, not per-object ones, and threading a logger
/// through every derive and record path to carry a handful of warnings would cost more than it explains.
/// </para>
/// </summary>
public static class LoomLog
{
	/// <summary>
	/// Where messages go. <c>null</c> (the default) writes to the console: <see cref="LoomLogLevel.Info"/> to stdout,
	/// warnings and errors to stderr so a shell can separate them.
	/// </summary>
	public static Action<LoomLogLevel, string>? Sink { get; set; }

	/// <summary>Something worth knowing about the frame or the graph — a device choice, a hot reload.</summary>
	public static void Info(string message) => Write(LoomLogLevel.Info, message);

	/// <summary>Something suspicious that is not fatal — a dead pass, a requested feature that was unavailable.</summary>
	public static void Warning(string message) => Write(LoomLogLevel.Warning, message);

	/// <summary>Something went wrong but did not stop the frame. A condition that should stop it throws instead.</summary>
	public static void Error(string message) => Write(LoomLogLevel.Error, message);

	private static void Write(LoomLogLevel level, string message)
	{
		var sink = Sink;
		if (sink is not null)
		{
			sink(level, message);
			return;
		}

		var line = level switch
		{
			LoomLogLevel.Warning => $"[loom] warning: {message}",
			LoomLogLevel.Error => $"[loom] error: {message}",
			_ => $"[loom] {message}",
		};
		if (level == LoomLogLevel.Info)
			Console.Out.WriteLine(line);
		else
			Console.Error.WriteLine(line);
	}
}
