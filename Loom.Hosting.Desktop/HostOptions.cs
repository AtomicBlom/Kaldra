using System.Globalization;

using Loom.Core.Hosting;

namespace Loom.Hosting.Desktop;

/// <summary>
/// <see cref="DesktopAppHost"/>'s command-line launch-option parser — the host-level flags every desktop-hosted
/// <see cref="LoomApplication"/> gets for free (backend selection, presentation, headless smoke/capture) without
/// the app author writing any parsing of its own. Modeled on <c>Loom.Sandbox</c>'s own launch-option grammar, so a
/// game hosted here is verifiable with the same golden/parity pipeline the engine's sandbox uses.
/// <para>
/// Parsing is deliberately tiny and dependency-free: <c>--key value</c>, <c>--key=value</c>, and bare
/// <c>--flag</c> (optionally <c>--flag=false</c>). Any argument this parser does not recognize as one of its own
/// flags is copied verbatim into <see cref="AppArgs"/> instead of rejected, so the app's own flags (e.g. a demo
/// selector) survive untouched and in order.
/// </para>
/// </summary>
public sealed record HostOptions
{
	/// <summary>
	/// The backend to select, as the canonical <see cref="IRenderBackend.Name"/> (<c>"Vulkan"</c> or
	/// <c>"DirectX12"</c>), or null when <c>--backend</c> was not given — in which case the app's own
	/// <see cref="EngineSettings.PreferredBackend"/> (set in <c>Configure</c>, or left null for auto-select) stands.
	/// </summary>
	public string? Backend { get; init; }

	/// <summary>Present with v-sync (default on); <c>--no-vsync</c> or <c>--vsync off</c> selects the low-latency mode. Maps onto <see cref="EngineSettings.VSync"/>.</summary>
	public bool VSync { get; init; } = true;

	/// <summary>Frame-rate cap for the host's render loop (0 = unlimited). Maps onto <see cref="EngineSettings.MaxFramesPerSecond"/>, which the host applies to the window.</summary>
	public double MaxFramesPerSecond { get; init; }

	/// <summary>Request true HDR (scRGB) display output. Maps onto <see cref="EngineSettings.HdrOutput"/>.</summary>
	public bool HdrOutput { get; init; }

	/// <summary>Watch the loose shader directory and hot-reload rebuilt artifacts (dev only). Maps onto <see cref="EngineSettings.WatchShaderDirectory"/>.</summary>
	public bool WatchShaderDirectory { get; init; }

	/// <summary>Headless: render N frames (real delta) then exit. 0 = run interactively.</summary>
	public int SmokeFrames { get; init; }

	/// <summary>Capture one frame to this PNG path with frozen time, then exit. Null disables.</summary>
	public string? Capture { get; init; }

	/// <summary>Print usage and exit. Set by <c>--help</c>/<c>-h</c> or by any parse error.</summary>
	public bool ShowHelp { get; init; }

	/// <summary>
	/// Every argument this parser did not recognize as one of its own flags, verbatim and in original order — the
	/// hosted app's own command line (e.g. the sandbox's <c>--demo</c>). An app that wants its own options parses
	/// this array exactly as it would have parsed the raw <c>args</c>.
	/// </summary>
	public string[] AppArgs { get; init; } = [];

	public static string Usage =>
		"""
		Loom desktop host — reusable launch options for a LoomApplication.

		Usage: dotnet run --project <app> -- [host options] [app options]

		Backend:
		  --backend <vulkan|dx12>   Backend to run on (default: the app's own preference, or the first available).

		Presentation:
		  --no-vsync                Use the low-latency present mode (v-sync is on by default).
		  --vsync <on|off>          Explicit form of the above.
		  --max-fps <n>             Frame-rate cap for the render loop (0 = unlimited).
		  --hdr                     Request true HDR (scRGB) display output.

		Dev / debug:
		  --watch-shaders           Hot-reload rebuilt shader artifacts.

		Headless / capture:
		  --smoke-frames <n>        Render N frames then exit.
		  --capture <path>          Capture one frame to a PNG (frozen time) then exit.

		  -h, --help                Show this help.

		Any option not listed above is passed through unchanged to the app.
		""";

	/// <summary>
	/// Parse <paramref name="args"/> into options. Recognized flags are consumed; everything else — including a
	/// value that follows an unrecognized flag — is copied into <see cref="AppArgs"/> untouched. On a malformed
	/// value for one of <em>this</em> parser's own flags, writes the reason to stderr and returns a value with
	/// <see cref="ShowHelp"/> set so the caller can print usage and exit.
	/// </summary>
	public static HostOptions Parse(string[] args)
	{
		ArgumentNullException.ThrowIfNull(args);

		string? backend = null;
		var vsync = true;
		var maxFps = 0.0;
		var hdr = false;
		var watchShaders = false;
		var smokeFrames = 0;
		string? capture = null;
		var help = false;
		var appArgs = new List<string>();

		HostOptions Fail(string message)
		{
			Console.Error.WriteLine($"[loom] {message}");
			return new HostOptions { ShowHelp = true };
		}

		for (var i = 0; i < args.Length; i++)
		{
			var arg = args[i];
			var key = arg;
			string? inlineValue = null;
			if (arg.StartsWith("--", StringComparison.Ordinal) && arg.IndexOf('=') is var eq && eq > 0)
			{
				key = arg[..eq];
				inlineValue = arg[(eq + 1)..];
			}

			// A value option reads its inline "=value" or the next token; throws (caught below) if neither exists.
			string Value()
			{
				if (inlineValue is not null)
					return inlineValue;
				if (i + 1 < args.Length)
					return args[++i];
				throw new FormatException($"option '{key}' requires a value");
			}

			// A flag is true when bare; "--flag=false" / "--flag=off" can still turn it off.
			bool Flag() => inlineValue is null || ParseBool(inlineValue);

			try
			{
				switch (key.ToLowerInvariant())
				{
					case "--backend": backend = ParseBackend(Value()); break;
					case "--vsync": vsync = ParseBool(Value()); break;
					case "--no-vsync": vsync = false; break;
					case "--max-fps":
						maxFps = double.Parse(Value(), CultureInfo.InvariantCulture);
						if (maxFps < 0) return Fail("--max-fps must be non-negative.");
						break;
					case "--hdr": hdr = Flag(); break;
					case "--watch-shaders": watchShaders = Flag(); break;
					case "--smoke-frames":
						smokeFrames = int.Parse(Value(), CultureInfo.InvariantCulture);
						if (smokeFrames <= 0) return Fail("--smoke-frames must be a positive frame count (a headless run needs N > 0 frames to render then exit).");
						break;
					case "--capture": capture = Value(); break;
					case "--help" or "-h" or "-?": help = true; break;
					default:
						// Not one of ours — pass it through untouched (its own value token, if any, arrives on the
						// next iteration and is passed through in turn since it won't match a case here either).
						appArgs.Add(arg);
						break;
				}
			}
			catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
			{
				return Fail(ex.Message);
			}
		}

		return new HostOptions
		{
			Backend = backend,
			VSync = vsync,
			MaxFramesPerSecond = maxFps,
			HdrOutput = hdr,
			WatchShaderDirectory = watchShaders,
			SmokeFrames = smokeFrames,
			Capture = capture,
			ShowHelp = help,
			AppArgs = [.. appArgs],
		};
	}

	/// <summary>
	/// Apply the parsed presentation/backend flags onto <paramref name="settings"/> — call before the engine is
	/// created so v-sync, the frame-rate cap, HDR, and shader watching take effect. <see cref="Backend"/> is
	/// applied only when <c>--backend</c> was actually given, so an app's own <see cref="EngineSettings.PreferredBackend"/>
	/// (or its absence, meaning auto-select) stands otherwise; the rest are applied unconditionally, mirroring the
	/// sandbox's own convention that the command line is the full source of truth for these once parsed.
	/// </summary>
	public void ApplyTo(EngineSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);
		if (Backend is not null)
			settings.PreferredBackend = Backend;
		settings.VSync = VSync;
		settings.MaxFramesPerSecond = MaxFramesPerSecond;
		settings.HdrOutput = HdrOutput;
		settings.WatchShaderDirectory = WatchShaderDirectory;
	}

	private static string ParseBackend(string value) => value.ToLowerInvariant() switch
	{
		"vulkan" or "vk" => "Vulkan",
		"dx12" or "d3d12" or "directx12" or "directx" => "DirectX12",
		_ => throw new ArgumentException($"unknown backend '{value}' (expected vulkan or dx12)"),
	};

	private static bool ParseBool(string value) => value.ToLowerInvariant() switch
	{
		"1" or "true" or "on" or "yes" => true,
		"0" or "false" or "off" or "no" => false,
		_ => throw new ArgumentException($"expected a boolean (on/off), got '{value}'"),
	};
}
