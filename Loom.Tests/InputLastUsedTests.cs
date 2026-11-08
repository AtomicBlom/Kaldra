using Loom.Input;

namespace Loom.Tests;

/// <summary>
/// What the player has in hand and what a binding is, for prompts and rebinding: the device last used
/// (<see cref="InputSnapshot.LastDevice"/>, <see cref="InputSnapshot.LastPad"/>) and bindings that read the pad last used
/// (<see cref="Pad.Last"/>); bindings broken into controls and named; defaults and conflicts; capturing the next press.
/// </summary>
public class InputLastUsedTests
{
	[Test]
	public void The_last_device_used_follows_presses_not_drift()
	{
		var s = new InputSnapshot();
		s.LastDevice.ShouldBe(InputDevice.None);

		s.Advance([InputEvent.GamepadConnected(0), InputEvent.GamepadConnected(1)]);
		s.Advance([InputEvent.KeyDown(Key.W)]);
		s.LastDevice.ShouldBe(InputDevice.Keyboard);

		s.Advance([InputEvent.GamepadAxisMoved(1, GamepadAxis.LeftX, 0.08f), InputEvent.GamepadAxisMoved(1, GamepadAxis.RightTrigger, 0.1f)]);
		s.LastDevice.ShouldBe(InputDevice.Keyboard);   // a stick's drift, a finger resting on a trigger

		s.Advance([InputEvent.GamepadAxisMoved(1, GamepadAxis.RightTrigger, 0.6f)]);
		s.LastDevice.ShouldBe(InputDevice.Gamepad);
		s.LastPad.ShouldBe(1);

		s.Advance([InputEvent.PointerMove(100, 100, 0, PointerDeviceType.Mouse)]);
		s.Advance([InputEvent.PointerMove(103, 101, 0, PointerDeviceType.Mouse)]);
		s.LastDevice.ShouldBe(InputDevice.Gamepad);    // a nudged mouse
		s.Advance([InputEvent.PointerMove(140, 120, 0, PointerDeviceType.Mouse)]);
		s.LastDevice.ShouldBe(InputDevice.Mouse);

		s.Advance([InputEvent.GamepadButtonDown(0, GamepadButton.South)]);
		s.LastPad.ShouldBe(0);
		s.Advance([InputEvent.GamepadDisconnected(0)]);
		s.LastPad.ShouldBe(-1);
	}

	[Test]
	public void Pad_bindings_read_whichever_pad_was_used_last()
	{
		var s = new InputSnapshot();
		var actions = InputActions.Define(b =>
		{
			b.Axis("throttle", Pad.RightTrigger, Pad.South);
			b.Button("handbrake", Pad.West);
		});
		var throttle = actions.Axis("throttle");
		var handbrake = actions.Button("handbrake");

		s.Advance([InputEvent.GamepadConnected(0), InputEvent.GamepadConnected(1)]);
		s.Advance([InputEvent.GamepadAxisMoved(1, GamepadAxis.RightTrigger, 0.8f)]);
		actions.Resolve(s);
		actions.Value(throttle).ShouldBe(0.8f);   // pad 1, the one in hand, not pad 0

		s.Advance([InputEvent.GamepadButtonDown(0, GamepadButton.West)]);
		actions.Resolve(s);
		actions.Pressed(handbrake).ShouldBeTrue();
		s.Pressed(GamepadButton.West).ShouldBeTrue();   // the snapshot's own pad queries too
		actions.Value(throttle).ShouldBe(0f);           // pad 0's trigger, untouched
	}

	[Test]
	public void Bindings_break_into_controls_named_as_the_pad_labels_them()
	{
		Composite.Axis(Key.A, Key.D).Controls().ShouldBe([InputControl.Of(Key.A), InputControl.Of(Key.D)]);
		Pad.LeftStick.Controls().ShouldBe([new InputControl(ControlKind.PadStick, 0)]);
		((InputBinding)Key.Space).Controls().Single().Name().ShouldBe("Space");
		((InputBinding)Key.Digit1).Controls().Single().Name().ShouldBe("1");

		var south = InputControl.Of(GamepadButton.South);
		south.Name(GamepadType.PS5).ShouldBe("Cross");
		south.Name(GamepadType.XboxOne).ShouldBe("A");
		south.Name(GamepadType.SwitchPro).ShouldBe("B");
		InputControl.Of(GamepadButton.Back).Name(GamepadType.PS5).ShouldBe("Create");
		InputControl.Of(GamepadButton.Back).Name(GamepadType.PS4).ShouldBe("Share");
		InputControl.Of(GamepadAxis.RightTrigger).Name(GamepadType.PS5).ShouldBe("R2");
		InputControl.Of(GamepadAxis.RightTrigger).Name(GamepadType.Unknown).ShouldBe("RT");
		InputControl.Of(GamepadButton.LeftPaddle1).Name(GamepadType.Steam).ShouldBe("Left grip");
		InputControl.Of(GamepadAxis.LeftX, -1).Name().ShouldBe("Left stick left");
	}

	[Test]
	public void Actions_reset_to_their_defaults_and_say_who_uses_a_control()
	{
		var actions = InputActions.Define(b =>
		{
			b.Axis("steer", Composite.Axis(Key.A, Key.D), Pad.LeftStick);
			b.Button("handbrake", Key.Space, Pad.West);
			b.Button("reset", Key.Backspace, Pad.North);
		});

		actions.ActionsUsing(InputControl.Of(Key.D)).ShouldBe(["steer"]);
		actions.ActionsUsing(InputControl.Of(GamepadAxis.LeftX, 1)).ShouldBe(["steer"]);   // a direction of the stick
		actions.ActionsUsing(InputControl.Of(GamepadButton.West)).ShouldBe(["handbrake"]);
		actions.ActionsUsing(InputControl.Of(Key.Q)).ShouldBeEmpty();

		actions.Rebind("handbrake", Key.H, Pad.East);
		actions.Bindings("handbrake").ShouldBe([(InputBinding)Key.H, Pad.East]);
		actions.ResetToDefaults("handbrake");
		actions.Bindings("handbrake").ShouldBe([(InputBinding)Key.Space, Pad.West]);

		actions.Rebind("reset", Key.R);
		actions.Rebind("steer", Composite.Axis(Key.Left, Key.Right));
		actions.ResetToDefaults();
		actions.Bindings("reset").ShouldBe(actions.Defaults("reset"));
		actions.Bindings("steer").ShouldBe(actions.Defaults("steer"));
	}

	[Test]
	public void A_capture_ignores_what_was_held_and_takes_the_next_press()
	{
		var s = new InputSnapshot();
		s.Advance([InputEvent.GamepadConnected(0), InputEvent.KeyDown(Key.Enter)]);
		var capture = new BindingCapture(InputDevice.Keyboard | InputDevice.Gamepad, s);

		s.Advance([]);
		capture.Poll(s).ShouldBe(CaptureState.Waiting);   // Enter, which chose the row, is still held

		s.Advance([InputEvent.KeyUp(Key.Enter)]);
		capture.Poll(s).ShouldBe(CaptureState.Waiting);
		s.Advance([InputEvent.GamepadAxisMoved(0, GamepadAxis.LeftTrigger, 0.3f)]);
		capture.Poll(s).ShouldBe(CaptureState.Waiting);   // not far enough
		s.Advance([InputEvent.GamepadAxisMoved(0, GamepadAxis.LeftTrigger, 0.9f)]);
		capture.Poll(s).ShouldBe(CaptureState.Captured);
		capture.Captured.ShouldBe(InputControl.Of(GamepadAxis.LeftTrigger));
		capture.Pad.ShouldBe(0);
		capture.Captured.ToBinding().ShouldBe(Pad.LeftTrigger);

		var keys = new BindingCapture(InputDevice.Keyboard, s);
		s.Advance([InputEvent.GamepadButtonDown(0, GamepadButton.South)]);
		keys.Poll(s).ShouldBe(CaptureState.Waiting);      // only keys asked for
		s.Advance([InputEvent.KeyDown(Key.Escape)]);
		keys.Poll(s).ShouldBe(CaptureState.Cancelled);

		var stick = new BindingCapture(InputDevice.Gamepad, s);
		s.Advance([InputEvent.GamepadAxisMoved(0, GamepadAxis.LeftX, -0.9f)]);
		stick.Poll(s).ShouldBe(CaptureState.Captured);
		stick.Captured.ShouldBe(InputControl.Of(GamepadAxis.LeftX, -1));
	}
}
