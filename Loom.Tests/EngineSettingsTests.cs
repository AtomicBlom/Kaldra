using Loom.Core.Hosting;

namespace Loom.Tests;

/// <summary>
/// <see cref="EngineSettings"/> defaults and the base-settings copy. <see cref="EngineSettings.CopyBaseSettingsTo"/>
/// exists so a backend building its own settings subclass cannot silently drop a base knob, which only holds if every
/// base setting is actually listed there — so each one is asserted.
/// </summary>
public class EngineSettingsTests
{
	// Deliberately not reflection over the properties: a reflective sweep would need annotating to survive trimming,
	// and the point of the list is that adding a setting forces someone to touch this file.
	private static readonly (string Name, Func<EngineSettings, object?> Read)[] BaseSettings =
	[
		(nameof(EngineSettings.EnableValidation), s => s.EnableValidation),
		(nameof(EngineSettings.ValidateRecording), s => s.ValidateRecording),
		(nameof(EngineSettings.VSync), s => s.VSync),
		(nameof(EngineSettings.MaxFramesPerSecond), s => s.MaxFramesPerSecond),
		(nameof(EngineSettings.HdrOutput), s => s.HdrOutput),
		(nameof(EngineSettings.PreferredBackend), s => s.PreferredBackend),
		(nameof(EngineSettings.WatchShaderDirectory), s => s.WatchShaderDirectory),
		(nameof(EngineSettings.PreferSoftwareAdapter), s => s.PreferSoftwareAdapter),
		(nameof(EngineSettings.PersistentPipelineCache), s => s.PersistentPipelineCache),
	];

	[Test]
	public void Record_validation_is_on_by_default()
	{
		// On by default because what it catches is otherwise silent — a binding the pass never declared reads in
		// whatever layout the previous pass left, which is wrong pixels rather than a crash.
		new EngineSettings().ValidateRecording.ShouldBeTrue();
	}

	[Test]
	public void Record_validation_is_independent_of_the_driver_debug_layer()
	{
		// Two different things: one enables the GPU driver's validation layer, the other checks Loom's own record-time
		// contract. A host that turns the driver layer off (as the hosted-app path does) must still get these checks.
		var settings = new EngineSettings { EnableValidation = false };

		settings.ValidateRecording.ShouldBeTrue();
	}

	[Test]
	public void Every_base_setting_survives_the_copy()
	{
		// Each value differs from its default, so a setting the copy forgot shows up as the default rather than
		// coincidentally matching.
		var source = new EngineSettings
		{
			EnableValidation = false,
			ValidateRecording = false,
			VSync = false,
			MaxFramesPerSecond = 144,
			HdrOutput = true,
			PreferredBackend = "DirectX12",
			WatchShaderDirectory = true,
			PreferSoftwareAdapter = true,
			PersistentPipelineCache = false,
		};
		var target = new EngineSettings();

		source.CopyBaseSettingsTo(target);

		foreach (var (name, read) in BaseSettings)
			read(target).ShouldBe(read(source), $"{name} was not copied by CopyBaseSettingsTo.");
	}

	[Test]
	public void The_copy_rejects_a_null_target()
	{
		Should.Throw<ArgumentNullException>(() => new EngineSettings().CopyBaseSettingsTo(null!));
	}
}
