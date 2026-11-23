using System.Numerics;
using BinaryVibrance.Kaldra.Input;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>Edge detection on <see cref="InputSnapshot"/>: Down (level) vs Pressed/Released (edges) as an event
/// stream is folded across frames, plus pointer delta, wheel reset, and gamepad hotplug edges.</summary>
public class InputSnapshotEdgeTests
{
	[Test]
	public void Key_reports_press_hold_and_release_across_frames()
	{
		var snap = new InputSnapshot();

		snap.Advance([InputEvent.KeyDown(Key.A)]);
		snap.Down(Key.A).ShouldBeTrue();
		snap.Pressed(Key.A).ShouldBeTrue();
		snap.Released(Key.A).ShouldBeFalse();

		// Held with no event this frame: still down, but no longer a fresh press.
		snap.Advance([]);
		snap.Down(Key.A).ShouldBeTrue();
		snap.Pressed(Key.A).ShouldBeFalse();
		snap.Released(Key.A).ShouldBeFalse();

		snap.Advance([InputEvent.KeyUp(Key.A)]);
		snap.Down(Key.A).ShouldBeFalse();
		snap.Released(Key.A).ShouldBeTrue();

		snap.Advance([]);
		snap.Released(Key.A).ShouldBeFalse();
	}

	[Test]
	public void Mouse_button_edges_track_transitions()
	{
		var snap = new InputSnapshot();

		snap.Advance([InputEvent.MouseButtonDown(MouseButton.Left)]);
		snap.Pressed(MouseButton.Left).ShouldBeTrue();
		snap.Down(MouseButton.Left).ShouldBeTrue();

		snap.Advance([InputEvent.MouseButtonUp(MouseButton.Left)]);
		snap.Released(MouseButton.Left).ShouldBeTrue();
		snap.Down(MouseButton.Left).ShouldBeFalse();
	}

	[Test]
	public void Pointer_delta_is_the_move_since_last_frame_and_wheel_resets()
	{
		var snap = new InputSnapshot();

		snap.Advance([InputEvent.PointerMove(10f, 20f)]);
		snap.PointerPosition.ShouldBe(new Vector2(10f, 20f));
		snap.PointerDelta.ShouldBe(new Vector2(10f, 20f));

		snap.Advance([InputEvent.PointerMove(15f, 25f), InputEvent.Wheel(0f, 3f)]);
		snap.PointerDelta.ShouldBe(new Vector2(5f, 5f));
		snap.Wheel.Y.ShouldBe(3f);

		// Wheel is a per-frame delta, not a level: it resets when no wheel event arrives.
		snap.Advance([]);
		snap.Wheel.Y.ShouldBe(0f);
		snap.PointerDelta.ShouldBe(Vector2.Zero);
	}

	[Test]
	public void Gamepad_reports_button_axis_and_hotplug_edges()
	{
		var snap = new InputSnapshot();

		snap.Advance([InputEvent.GamepadConnected(0), InputEvent.GamepadButtonDown(0, GamepadButton.South)]);
		var pad = snap.Gamepad(0);
		pad.Connected.ShouldBeTrue();
		pad.JustConnected.ShouldBeTrue();
		pad.Pressed(GamepadButton.South).ShouldBeTrue();

		snap.Advance([InputEvent.GamepadAxisMoved(0, GamepadAxis.LeftX, 0.5f)]);
		pad = snap.Gamepad(0);
		pad.Axis(GamepadAxis.LeftX).ShouldBe(0.5f);
		pad.JustConnected.ShouldBeFalse();
		pad.Down(GamepadButton.South).ShouldBeTrue();
		pad.Pressed(GamepadButton.South).ShouldBeFalse();

		snap.Advance([InputEvent.GamepadDisconnected(0)]);
		pad = snap.Gamepad(0);
		pad.Connected.ShouldBeFalse();
		pad.JustDisconnected.ShouldBeTrue();
	}

	[Test]
	public void Text_events_carry_a_utf32_code_point()
	{
		// A typed 'A' (U+0041) rides its own event kind, distinct from the KeyDown that produced it.
		var e = InputEvent.Text(0x41);
		e.Kind.ShouldBe(InputEventKind.Text);
		e.CodePoint.ShouldBe(0x41u);
		e.Device.ShouldBe(InputDevice.Keyboard);
	}

	[Test]
	public void Disconnecting_a_pad_releases_state_held_at_unplug()
	{
		var snap = new InputSnapshot();

		snap.Advance(
		[
			InputEvent.GamepadConnected(0),
			InputEvent.GamepadButtonDown(0, GamepadButton.South),
			InputEvent.GamepadAxisMoved(0, GamepadAxis.LeftX, 0.9f),
		]);
		snap.Gamepad(0).Down(GamepadButton.South).ShouldBeTrue();

		// The pad vanishes while South is still held (no button-up arrives). The snapshot must synthesize a clean
		// release edge and drop the axis, not strand the button as permanently down.
		snap.Advance([InputEvent.GamepadDisconnected(0)]);
		var pad = snap.Gamepad(0);
		pad.Connected.ShouldBeFalse();
		pad.JustDisconnected.ShouldBeTrue();
		pad.Down(GamepadButton.South).ShouldBeFalse();
		pad.Released(GamepadButton.South).ShouldBeTrue();
		pad.Axis(GamepadAxis.LeftX).ShouldBe(0f);
	}
}
