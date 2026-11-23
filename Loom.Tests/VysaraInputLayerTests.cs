using System.Numerics;

using Loom.Input;
using Loom.UI.Vysara;

using Vysara;
using Vysara.Controls;
using Vysara.Media;

namespace Loom.Tests;

/// <summary>
/// What <see cref="VysaraInputLayer"/> takes from the game: a page with two buttons stacked at its top left, over a game
/// layer that records what reaches it, run through an <see cref="InputLayerStack"/> a frame at a time as an app does.
/// </summary>
public class VysaraInputLayerTests
{
	private static readonly Vector2 Surface = new(400f, 400f);
	private static readonly Vector2 OnFirst = new(50f, 25f);   // inside the first button (0..100 x 0..50)
	private static readonly Vector2 Empty = new(300f, 300f);   // on no content

	private sealed class Page
	{
		public UIHost Host { get; } = new();
		public Button First { get; }
		public Button Second { get; }
		public int FirstClicks { get; private set; }
		public int SecondClicks { get; private set; }
		public VysaraInputLayer Layer { get; }
		public Recorder Game { get; } = new();
		public InputLayerStack Stack { get; } = new();
		private float _time;

		public Page()
		{
			First = MakeButton();
			Second = MakeButton();
			First.Click += (_, _) => FirstClicks++;
			Second.Click += (_, _) => SecondClicks++;
			var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
			column.Children.Add(First);
			column.Children.Add(Second);
			Host.CreateRoot(UILayer.Default).Root.Content = column;
			Layer = new VysaraInputLayer(Host);
			Stack.Push(Game);
			Stack.Push(Layer);
			Host.Tick(0.016, 0f, Surface); // lay out, so content can be hit
		}

		private static Button MakeButton() =>
			new() { Width = 100f, Height = 50f, Background = new SolidColorBrush(HdrColor.FromRgb(50, 100, 200)) };

		// One frame: the input resolved through the stack, then the UI's tick, as LoomApplication runs them.
		public void Frame(params InputEvent[] events)
		{
			Game.Seen.Clear();
			Stack.Resolve(events);
			Host.Tick(0.016, _time += 0.016f, Surface);
		}
	}

	// The game: everything that reaches the bottom of the stack.
	private sealed class Recorder : IInputLayer
	{
		public List<InputEvent> Seen { get; } = [];

		public void Process(ref InputContext ctx)
		{
			foreach (ref readonly var e in ctx.Incoming)
				Seen.Add(e);
		}
	}

	// A finger, as every host sends one: touch events only, never the mouse.
	private static InputEvent TouchDown(int id, Vector2 at) => InputEvent.Touch(TouchPhase.Began, id, at.X, at.Y);
	private static InputEvent TouchUp(int id, Vector2 at) => InputEvent.Touch(TouchPhase.Ended, id, at.X, at.Y);

	[Test]
	public void A_tap_on_a_button_clicks_it_and_the_game_sees_none_of_it()
	{
		var page = new Page();

		page.Frame(TouchDown(0, OnFirst));
		var seenDown = page.Game.Seen.ToList();
		page.Frame(TouchUp(0, OnFirst));

		page.FirstClicks.ShouldBe(1);
		seenDown.ShouldBeEmpty();
		page.Game.Seen.ShouldBeEmpty();
	}

	[Test]
	public void A_finger_on_empty_space_reaches_the_game_while_another_holds_a_button()
	{
		var page = new Page();

		page.Frame(TouchDown(0, OnFirst), TouchDown(1, Empty));

		page.Game.Seen.ShouldContain(e => e.Kind == InputEventKind.Touch && e.PointerId == 1);
		page.Game.Seen.ShouldNotContain(e => e.Kind == InputEventKind.Touch && e.PointerId == 0);
	}

	[Test]
	public void A_tap_on_empty_space_is_the_game_s()
	{
		var page = new Page();

		page.Frame(TouchDown(0, Empty));

		page.Game.Seen.ShouldContain(e => e.Kind == InputEventKind.Touch);
	}

	[Test]
	public void A_mouse_press_on_a_button_never_reaches_the_game_even_on_its_first_frame()
	{
		var page = new Page();
		page.Frame(InputEvent.PointerMove(Empty.X, Empty.Y, 0, PointerDeviceType.Mouse));

		page.Frame(InputEvent.PointerMove(OnFirst.X, OnFirst.Y, 0, PointerDeviceType.Mouse), InputEvent.MouseButtonDown(MouseButton.Left));

		page.Game.Seen.ShouldNotContain(e => e.Kind == InputEventKind.MouseButtonDown);
	}

	[Test]
	public void With_the_pad_taken_the_d_pad_moves_focus_and_a_clicks_and_the_game_sees_neither()
	{
		var page = new Page();
		page.Layer.PadNavigation = true;
		page.Host.Focus.TrySetFocus(page.First, FocusState.Programmatic).ShouldBeTrue();

		page.Frame(InputEvent.GamepadButtonDown(0, GamepadButton.DPadDown));
		var seenMove = page.Game.Seen.ToList();
		page.Frame(InputEvent.GamepadButtonUp(0, GamepadButton.DPadDown));
		page.Host.Focus.FocusedElement.ShouldBe(page.Second);

		page.Frame(InputEvent.GamepadButtonDown(0, GamepadButton.South));
		page.Frame(InputEvent.GamepadButtonUp(0, GamepadButton.South));

		page.SecondClicks.ShouldBe(1);
		seenMove.ShouldBeEmpty();
		page.Game.Seen.ShouldBeEmpty();
	}

	[Test]
	public void The_left_stick_moves_focus_like_the_d_pad()
	{
		var page = new Page();
		page.Layer.PadNavigation = true;
		page.Host.Focus.TrySetFocus(page.First, FocusState.Programmatic).ShouldBeTrue();

		page.Frame(InputEvent.GamepadAxisMoved(0, GamepadAxis.LeftY, 0.8f)); // down: y is down
		page.Host.Focus.FocusedElement.ShouldBe(page.Second);

		page.Frame(InputEvent.GamepadAxisMoved(0, GamepadAxis.LeftY, 0.4f)); // eased off, but not back inside the release
		page.Frame(InputEvent.GamepadAxisMoved(0, GamepadAxis.LeftY, 0.8f));
		page.Host.Focus.FocusedElement.ShouldBe(page.Second); // still the one push
	}

	[Test]
	public void Without_the_pad_taken_and_nothing_focused_the_pad_is_the_game_s()
	{
		var page = new Page();

		page.Frame(InputEvent.GamepadButtonDown(0, GamepadButton.DPadDown));

		page.Game.Seen.ShouldContain(e => e.Kind == InputEventKind.GamepadButtonDown);
		page.Host.Focus.FocusedElement.ShouldBeNull();
	}

	[Test]
	public void Focus_from_a_tap_does_not_take_the_pad_from_the_game()
	{
		var page = new Page();
		page.Frame(TouchDown(0, OnFirst));
		page.Frame(TouchUp(0, OnFirst));
		page.Host.Focus.FocusState.ShouldBe(FocusState.Pointer);

		page.Frame(InputEvent.GamepadButtonDown(0, GamepadButton.South));

		page.Game.Seen.ShouldContain(e => e.Kind == InputEventKind.GamepadButtonDown);
		page.FirstClicks.ShouldBe(1); // the tap's, not the pad's
	}
}
