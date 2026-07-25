using Loom.Input;

namespace Loom.Tests;

/// <summary>Binding persistence: a rebinding overlay survives a <see cref="InputActions.SaveBindings"/> /
/// <see cref="InputActions.LoadBindings"/> round-trip through the source-generated JSON.</summary>
public class InputBindingsPersistenceTests
{
	private static InputActions DefaultSet() => InputActions.Define(b =>
	{
		b.Button("jump", Key.Space);
		b.Vector("move", Composite.Wasd).Deadzone(Deadzone.Radial(0.2f));
	});

	[Test]
	public void Rebinding_round_trips_through_json()
	{
		var actions = DefaultSet();
		actions.Rebind("jump", Key.Enter);
		var json = actions.SaveBindings();
		json.ShouldContain("jump"); // action names are serialized verbatim

		var loaded = DefaultSet();
		loaded.LoadBindings(json);

		var jump = loaded.Button("jump");

		var enterHeld = new InputSnapshot();
		enterHeld.Advance([InputEvent.KeyDown(Key.Enter)]);
		loaded.Resolve(enterHeld);
		loaded.Down(jump).ShouldBeTrue(); // the rebound key survived

		var spaceHeld = new InputSnapshot();
		spaceHeld.Advance([InputEvent.KeyDown(Key.Space)]);
		loaded.Resolve(spaceHeld);
		loaded.Down(jump).ShouldBeFalse(); // the original key no longer triggers
	}

	[Test]
	public void Loaded_bindings_match_the_source_set_exactly()
	{
		var actions = DefaultSet();
		actions.Rebind("jump", Key.Enter);

		var loaded = DefaultSet();
		loaded.LoadBindings(actions.SaveBindings());

		// Re-serializing the loaded set reproduces the source set byte-for-byte (bindings, deadzone, and curve).
		loaded.SaveBindings().ShouldBe(actions.SaveBindings());
	}

	[Test]
	public void Unknown_action_names_in_the_document_are_ignored()
	{
		var loaded = DefaultSet();
		// A document naming an action this set does not define must not throw.
		Should.NotThrow(() => loaded.LoadBindings("{\"Actions\":[{\"Name\":\"gone\",\"Sources\":[]}]}"));
	}
}
