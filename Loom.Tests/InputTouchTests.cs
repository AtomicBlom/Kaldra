using System.Numerics;

using Loom.Input;
using Loom.UI.SUIsei;

using SUIsei;
using SUIsei.Controls;

namespace Loom.Tests;

/// <summary>
/// Touch through the input stack: a contact's phases in <see cref="InputSnapshot.Touches"/> frame to frame, an action
/// pressed from outside its bindings (<see cref="InputActions.Press"/>), and fingers shared between SUIsei and the game
/// contact by contact (<see cref="SUIseiInputLayer"/>).
/// </summary>
public class InputTouchTests
{
	private static readonly Vector2 Surface = new(400f, 300f);

	[Test]
	public void A_contact_begins_for_one_frame_then_moves_or_stands_until_it_ends()
	{
		var snap = new InputSnapshot();

		snap.Advance([InputEvent.Touch(TouchPhase.Began, 7, 10f, 20f)]);
		var t = snap.Touches.ToArray().ShouldHaveSingleItem();
		t.Phase.ShouldBe(TouchPhase.Began);
		t.BeganThisFrame.ShouldBeTrue();
		t.Start.ShouldBe(new Vector2(10f, 20f));

		snap.Advance([]);
		t = snap.Touches[0];
		t.Phase.ShouldBe(TouchPhase.Stationary);
		t.BeganThisFrame.ShouldBeFalse();
		t.Delta.ShouldBe(Vector2.Zero);

		snap.Advance([InputEvent.Touch(TouchPhase.Moved, 7, 15f, 18f)]);
		t = snap.Touches[0];
		t.Phase.ShouldBe(TouchPhase.Moved);
		t.Delta.ShouldBe(new Vector2(5f, -2f));
		t.Start.ShouldBe(new Vector2(10f, 20f));

		snap.Advance([InputEvent.Touch(TouchPhase.Ended, 7, 15f, 18f)]);
		snap.Touches[0].Phase.ShouldBe(TouchPhase.Ended);

		snap.Advance([]);
		snap.Touches.Length.ShouldBe(0);
	}

	[Test]
	public void A_tap_within_one_frame_is_reported_ended_and_begun_this_frame()
	{
		var snap = new InputSnapshot();
		snap.Advance([InputEvent.Touch(TouchPhase.Began, 1, 5f, 5f), InputEvent.Touch(TouchPhase.Moved, 1, 6f, 5f), InputEvent.Touch(TouchPhase.Ended, 1, 6f, 5f)]);
		var t = snap.Touches.ToArray().ShouldHaveSingleItem();
		t.Gone.ShouldBeTrue();
		t.BeganThisFrame.ShouldBeTrue();
	}

	[Test]
	public void A_moved_contact_that_began_this_frame_still_reads_began()
	{
		var snap = new InputSnapshot();
		snap.Advance([InputEvent.Touch(TouchPhase.Began, 1, 5f, 5f), InputEvent.Touch(TouchPhase.Moved, 1, 9f, 5f)]);
		snap.Touches[0].Phase.ShouldBe(TouchPhase.Began);
		snap.Touches[0].Delta.ShouldBe(new Vector2(4f, 0f));
	}

	[Test]
	public void An_up_for_a_contact_never_seen_is_dropped()
	{
		var snap = new InputSnapshot();
		snap.Advance([InputEvent.Touch(TouchPhase.Ended, 3, 5f, 5f)]);
		snap.Touches.Length.ShouldBe(0);
	}

	[Test]
	public void A_touch_makes_touch_the_device_last_used()
	{
		var snap = new InputSnapshot();
		snap.Advance([InputEvent.KeyDown(Key.A)]);
		snap.Advance([InputEvent.Touch(TouchPhase.Began, 1, 5f, 5f)]);
		snap.LastDevice.ShouldBe(InputDevice.Touch);
	}

	[Test]
	public void An_action_pressed_from_outside_is_pressed_once_then_released()
	{
		var actions = InputActions.Define(b => b.Button("go", Key.Enter));
		var go = actions.Button("go");
		var snap = new InputSnapshot();

		actions.Press(go);
		snap.Advance([]);
		actions.Resolve(snap);
		actions.Pressed(go).ShouldBeTrue();
		actions.Down(go).ShouldBeTrue();

		snap.Advance([]);
		actions.Resolve(snap);
		actions.Down(go).ShouldBeFalse();
		actions.Released(go).ShouldBeTrue();
	}

	private static (UIHost Host, InputLayerStack Stack, Button Button) Page()
	{
		var host = new UIHost();
		var view = host.CreateRoot(UILayer.Default);
		var button = new Button { Width = 80f, Height = 30f, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
		view.Root.Content = button;
		host.Tick(0.016, 0f, Surface);
		var stack = new InputLayerStack();
		stack.Push(new SUIseiInputLayer(host));
		return (host, stack, button);
	}

	private static InputSnapshot Frame(UIHost host, InputLayerStack stack, params InputEvent[] events)
	{
		var output = stack.Resolve(events);
		host.Tick(0.016, 0f, Surface);
		return output;
	}

	[Test]
	public void A_tap_on_a_button_clicks_it_and_is_kept_from_the_game()
	{
		var (host, stack, button) = Page();
		int clicks = 0;
		button.Click += (_, _) => clicks++;

		Frame(host, stack, InputEvent.Touch(TouchPhase.Began, 1, 20f, 10f)).Touches.Length.ShouldBe(0);
		Frame(host, stack, InputEvent.Touch(TouchPhase.Ended, 1, 20f, 10f)).Touches.Length.ShouldBe(0);
		clicks.ShouldBe(1);
		host.WantsPointer.ShouldBeFalse();   // a lifted finger hovers over nothing
	}

	[Test]
	public void A_finger_on_the_game_stays_the_games_while_another_holds_a_button()
	{
		var (host, stack, _) = Page();

		Frame(host, stack, InputEvent.Touch(TouchPhase.Began, 1, 20f, 10f));
		var game = Frame(host, stack, InputEvent.Touch(TouchPhase.Began, 2, 300f, 200f));
		game.Touches.ToArray().ShouldHaveSingleItem().Id.ShouldBe(2);

		// Dragged over the button, it is still the game's
		game = Frame(host, stack, InputEvent.Touch(TouchPhase.Moved, 2, 20f, 10f));
		game.Touches.ToArray().ShouldHaveSingleItem().Phase.ShouldBe(TouchPhase.Moved);
	}
}
