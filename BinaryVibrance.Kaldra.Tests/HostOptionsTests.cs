using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Hosting.Desktop;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// <see cref="HostOptions"/> launch-option parsing — the reusable desktop host's command line. Pure and
/// dependency-free (no window or GPU), so it is exercised directly: recognized flags are consumed, unknown ones
/// pass through verbatim, and a malformed value for one of its own flags sets <see cref="HostOptions.ShowHelp"/>.
/// </summary>
public class HostOptionsTests
{
	[Test]
	public void Defaults_apply_when_no_args_are_given()
	{
		var o = HostOptions.Parse([]);

		o.Backend.ShouldBeNull();
		o.VSync.ShouldBeTrue();
		o.MaxFramesPerSecond.ShouldBe(0);
		o.HdrOutput.ShouldBeFalse();
		o.WatchShaderDirectory.ShouldBeFalse();
		o.SoftwareAdapter.ShouldBeFalse();
		o.ShowHelp.ShouldBeFalse();
		o.AppArgs.ShouldBeEmpty();
	}

	[Test]
	[Arguments("vulkan", "Vulkan")]
	[Arguments("vk", "Vulkan")]
	[Arguments("VULKAN", "Vulkan")]
	[Arguments("dx12", "DirectX12")]
	[Arguments("d3d12", "DirectX12")]
	[Arguments("directx12", "DirectX12")]
	[Arguments("DirectX", "DirectX12")]
	public void Backend_aliases_resolve_to_the_canonical_name(string value, string expected)
	{
		HostOptions.Parse(["--backend", value]).Backend.ShouldBe(expected);
		HostOptions.Parse([$"--backend={value}"]).Backend.ShouldBe(expected); // inline "=value" form
	}

	[Test]
	public void An_unknown_backend_reports_help_rather_than_guessing()
	{
		var o = HostOptions.Parse(["--backend", "metal"]);
		o.ShowHelp.ShouldBeTrue();
		o.Backend.ShouldBeNull();
	}

	[Test]
	public void Vsync_can_be_turned_off_by_flag_or_explicit_value()
	{
		HostOptions.Parse(["--no-vsync"]).VSync.ShouldBeFalse();
		HostOptions.Parse(["--vsync", "off"]).VSync.ShouldBeFalse();
		HostOptions.Parse(["--vsync", "on"]).VSync.ShouldBeTrue();
	}

	[Test]
	public void Max_fps_parses_a_number_and_rejects_a_negative_one()
	{
		HostOptions.Parse(["--max-fps", "144"]).MaxFramesPerSecond.ShouldBe(144);
		HostOptions.Parse(["--max-fps=60"]).MaxFramesPerSecond.ShouldBe(60);
		HostOptions.Parse(["--max-fps", "-1"]).ShowHelp.ShouldBeTrue();
		HostOptions.Parse(["--max-fps", "fast"]).ShowHelp.ShouldBeTrue(); // non-numeric
	}

	[Test]
	public void Bare_flags_are_true_and_the_equals_false_form_turns_them_off()
	{
		var on = HostOptions.Parse(["--hdr", "--watch-shaders", "--software-adapter"]);
		on.HdrOutput.ShouldBeTrue();
		on.WatchShaderDirectory.ShouldBeTrue();
		on.SoftwareAdapter.ShouldBeTrue();

		HostOptions.Parse(["--hdr=false"]).HdrOutput.ShouldBeFalse();
		HostOptions.Parse(["--hdr=off"]).HdrOutput.ShouldBeFalse();
	}

	[Test]
	[Arguments("--help")]
	[Arguments("-h")]
	[Arguments("-?")]
	public void Help_flags_set_show_help(string flag) => HostOptions.Parse([flag]).ShowHelp.ShouldBeTrue();

	[Test]
	public void Unrecognized_args_pass_through_verbatim_and_in_order()
	{
		// The host's own flags are consumed; everything else — including the value token after an unknown flag —
		// is copied into AppArgs untouched, so the hosted app parses it exactly as it would the raw args.
		var o = HostOptions.Parse(["--demo", "bloom", "--no-vsync", "--frustum", "42"]);

		o.VSync.ShouldBeFalse(); // the one recognized flag was consumed
		o.AppArgs.ShouldBe(["--demo", "bloom", "--frustum", "42"]);
	}

	[Test]
	public void Apply_to_maps_parsed_flags_onto_engine_settings()
	{
		var settings = new EngineSettings { PreferredBackend = "Vulkan" };
		HostOptions.Parse(["--backend", "dx12", "--no-vsync", "--max-fps", "120", "--hdr", "--software-adapter"])
			.ApplyTo(settings);

		settings.PreferredBackend.ShouldBe("DirectX12");
		settings.VSync.ShouldBeFalse();
		settings.MaxFramesPerSecond.ShouldBe(120);
		settings.HdrOutput.ShouldBeTrue();
		settings.PreferSoftwareAdapter.ShouldBeTrue();
	}

	[Test]
	public void Apply_to_leaves_the_apps_preferred_backend_when_no_backend_flag_is_given()
	{
		// Without --backend, the app's own PreferredBackend (set in Configure) must stand.
		var settings = new EngineSettings { PreferredBackend = "Vulkan" };
		HostOptions.Parse(["--hdr"]).ApplyTo(settings);
		settings.PreferredBackend.ShouldBe("Vulkan");
	}
}
