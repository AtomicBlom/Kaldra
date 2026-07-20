using System.Numerics;
using Loom.Input;

namespace Loom.Tests;

/// <summary>Action resolution against a snapshot: button edges (including the multi-source no-refire rule), scalar
/// axes with deadzone, and vector actions from the WASD composite and a gamepad stick.</summary>
public class InputActionResolutionTests
{
	[Test]
	public void Button_action_reports_edges_across_frames()
	{
		var actions = InputActions.Define(b => b.Button("jump", Key.Space, Pad.South));
		var jump = actions.Button("jump");
		var snap = new InputSnapshot();

		snap.Advance([InputEvent.KeyDown(Key.Space)]);
		actions.Resolve(snap);
		actions.Down(jump).ShouldBeTrue();
		actions.Pressed(jump).ShouldBeTrue();

		snap.Advance([]);
		actions.Resolve(snap);
		actions.Down(jump).ShouldBeTrue();
		actions.Pressed(jump).ShouldBeFalse();

		snap.Advance([InputEvent.KeyUp(Key.Space)]);
		actions.Resolve(snap);
		actions.Down(jump).ShouldBeFalse();
		actions.Released(jump).ShouldBeTrue();
	}

	[Test]
	public void Button_action_does_not_refire_when_a_second_source_engages()
	{
		var actions = InputActions.Define(b => b.Button("fire", Key.Space, Pad.South));
		var fire = actions.Button("fire");
		var snap = new InputSnapshot();

		snap.Advance([InputEvent.GamepadConnected(0), InputEvent.KeyDown(Key.Space)]);
		actions.Resolve(snap);
		actions.Pressed(fire).ShouldBeTrue();

		// A different bound source engaging while the action is already held is not a new press.
		snap.Advance([InputEvent.GamepadButtonDown(0, GamepadButton.South)]);
		actions.Resolve(snap);
		actions.Down(fire).ShouldBeTrue();
		actions.Pressed(fire).ShouldBeFalse();
	}

	[Test]
	public void Axis_action_from_a_key_pair_reads_minus_one_to_one()
	{
		var actions = InputActions.Define(b => b.Axis("throttle", Composite.Axis(Key.S, Key.W)));
		var throttle = actions.Axis("throttle");
		var snap = new InputSnapshot();

		snap.Advance([InputEvent.KeyDown(Key.W)]);
		actions.Resolve(snap);
		actions.Value(throttle).ShouldBe(1f, 0.0001);

		snap.Advance([InputEvent.KeyUp(Key.W), InputEvent.KeyDown(Key.S)]);
		actions.Resolve(snap);
		actions.Value(throttle).ShouldBe(-1f, 0.0001);
	}

	[Test]
	public void Axis_action_applies_its_deadzone_to_an_analog_source()
	{
		var actions = InputActions.Define(b => b.Axis("aim", GamepadAxis.RightX).Deadzone(Deadzone.Radial(0.2f)));
		var aim = actions.Axis("aim");
		var snap = new InputSnapshot();

		snap.Advance([InputEvent.GamepadConnected(0), InputEvent.GamepadAxisMoved(0, GamepadAxis.RightX, 0.1f)]);
		actions.Resolve(snap);
		actions.Value(aim).ShouldBe(0f, 0.0001);

		snap.Advance([InputEvent.GamepadAxisMoved(0, GamepadAxis.RightX, 0.6f)]);
		actions.Resolve(snap);
		actions.Value(aim).ShouldBe(0.5f, 0.0001); // (0.6-0.2)/0.8
	}

	[Test]
	public void Vector_action_resolves_the_wasd_composite()
	{
		var actions = InputActions.Define(b => b.Vector("move", Composite.Wasd));
		var move = actions.Vector("move");
		var snap = new InputSnapshot();

		snap.Advance([InputEvent.KeyDown(Key.D)]);
		actions.Resolve(snap);
		actions.Value(move).ShouldBe(new Vector2(1f, 0f));

		// D + W → (right, up) = (1, 1), clamped to unit length but keeping the diagonal.
		snap.Advance([InputEvent.KeyDown(Key.W)]);
		actions.Resolve(snap);
		var diagonal = actions.Value(move);
		diagonal.Length().ShouldBe(1f, 0.0001);
		(diagonal.X > 0f).ShouldBeTrue();
		(diagonal.Y > 0f).ShouldBeTrue();
	}

	[Test]
	public void Vector_action_deadzones_a_gamepad_stick()
	{
		var actions = InputActions.Define(b => b.Vector("move", Pad.LeftStick).Deadzone(Deadzone.Radial(0.2f)));
		var move = actions.Vector("move");
		var snap = new InputSnapshot();

		snap.Advance(
		[
			InputEvent.GamepadConnected(0),
			InputEvent.GamepadAxisMoved(0, GamepadAxis.LeftX, 0.1f),
			InputEvent.GamepadAxisMoved(0, GamepadAxis.LeftY, 0f),
		]);
		actions.Resolve(snap);
		actions.Value(move).ShouldBe(Vector2.Zero); // stick drift inside the deadzone

		snap.Advance([InputEvent.GamepadAxisMoved(0, GamepadAxis.LeftX, 1f)]);
		actions.Resolve(snap);
		actions.Value(move).X.ShouldBe(1f, 0.0001);
	}
}
