using System.Numerics;

using Loom.Input;
using Loom.UI.Vysara;

using BinaryVibrance.Vysara;
using BinaryVibrance.Vysara.Controls;

namespace Loom.Tests;

/// <summary>
/// A pad through Vysara (<see cref="VysaraInputLayer.PadNavigation"/>): the d-pad and the left stick move focus by
/// position, South presses the focused button, and the pad goes no further down the stack while the UI has it.
/// </summary>
public class VysaraPadNavigationTests
{
	private static readonly Vector2 Surface = new(400f, 300f);

	private static (UIHost Host, InputLayerStack Stack, VysaraInputLayer Layer, Button Left, Button Right) Page()
	{
		var host = new UIHost();
		var view = host.CreateRoot(UILayer.Default);
		var panel = new StackPanel { Orientation = Orientation.Horizontal };
		var left = new Button { Width = 80f, Height = 30f };
		var right = new Button { Width = 80f, Height = 30f };
		panel.Children.Add(left);
		panel.Children.Add(right);
		view.Root.Content = panel;
		host.Tick(0.016, 0f, Surface);
		var stack = new InputLayerStack();
		var layer = new VysaraInputLayer(host);
		stack.Push(layer);
		return (host, stack, layer, left, right);
	}

	private static InputSnapshot Frame(UIHost host, InputLayerStack stack, params InputEvent[] events)
	{
		var output = stack.Resolve(events);
		host.Tick(0.016, 0f, Surface);
		return output;
	}

	[Test]
	public void The_dpad_and_stick_move_focus_and_South_presses()
	{
		var (host, stack, layer, left, right) = Page();
		int clicks = 0;
		right.Click += (_, _) => clicks++;
		layer.PadNavigation = true;
		Frame(host, stack, InputEvent.GamepadConnected(0));

		Frame(host, stack, InputEvent.GamepadButtonDown(0, GamepadButton.DPadRight), InputEvent.GamepadButtonUp(0, GamepadButton.DPadRight));
		host.Focus.FocusedElement.ShouldBe(left);   // nothing focused: the first
		Frame(host, stack, InputEvent.GamepadAxisMoved(0, GamepadAxis.LeftX, 0.9f));
		host.Focus.FocusedElement.ShouldBe(right);
		Frame(host, stack, InputEvent.GamepadAxisMoved(0, GamepadAxis.LeftX, 0.8f));
		host.Focus.FocusedElement.ShouldBe(right);   // still held: no second move until it repeats

		Frame(host, stack, InputEvent.GamepadButtonDown(0, GamepadButton.South), InputEvent.GamepadButtonUp(0, GamepadButton.South));
		clicks.ShouldBe(1);
	}

	[Test]
	public void The_pad_reaches_the_game_only_while_the_UI_doesnt_have_it()
	{
		var (host, stack, layer, _, _) = Page();
		Frame(host, stack, InputEvent.GamepadConnected(0));

		layer.PadNavigation = true;
		Frame(host, stack, InputEvent.GamepadButtonDown(0, GamepadButton.South)).Gamepad(0).Down(GamepadButton.South).ShouldBeFalse();
		Frame(host, stack, InputEvent.GamepadButtonUp(0, GamepadButton.South));

		layer.PadNavigation = false;
		Frame(host, stack, InputEvent.GamepadButtonDown(0, GamepadButton.South)).Gamepad(0).Down(GamepadButton.South).ShouldBeTrue();
	}
}
