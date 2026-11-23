using BinaryVibrance.Kaldra.Input;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>The layer-stack resolution fold: coarse blocking, injection, block-boundary edge synthesis (the
/// hold-a-key-across-a-block test), a scripted-layer walkthrough, and the allocation-free steady path.</summary>
public class InputLayerStackTests
{
	[Test]
	public void An_upper_block_hides_a_device_from_lower_layers()
	{
		var stack = new InputLayerStack();
		var recorder = new KeyRecorder(Key.A);
		stack.Push(recorder);                      // bottom
		stack.Push(new DeviceBlocker(InputDevice.Keyboard)); // top, always blocks the keyboard

		var output = stack.Resolve([InputEvent.KeyDown(Key.A)]);

		recorder.Down.ShouldBeFalse();   // the lower layer never saw the key
		output.Down(Key.A).ShouldBeFalse();
	}

	[Test]
	public void An_upper_block_leaves_other_devices_untouched()
	{
		var stack = new InputLayerStack();
		var recorder = new KeyRecorder(Key.A);
		stack.Push(recorder);
		stack.Push(new DeviceBlocker(InputDevice.Mouse)); // blocks mouse, not keyboard

		stack.Resolve([InputEvent.KeyDown(Key.A)]);

		recorder.Down.ShouldBeTrue(); // keyboard still flows through
	}

	[Test]
	public void An_injected_event_reaches_lower_layers_as_if_real()
	{
		var stack = new InputLayerStack();
		var recorder = new KeyRecorder(Key.B);
		stack.Push(recorder);                                        // bottom
		stack.Push(new Injector(InputEvent.KeyDown(Key.B)));         // top, injects a key nobody pressed

		var output = stack.Resolve([]);

		recorder.Down.ShouldBeTrue();
		recorder.Pressed.ShouldBeTrue();
		output.Down(Key.B).ShouldBeTrue();
	}

	[Test]
	public void Holding_a_key_across_a_block_engage_then_disengage_synthesizes_clean_edges()
	{
		var stack = new InputLayerStack();
		var recorder = new KeyRecorder(Key.A);
		var blocker = new ToggleBlocker(InputDevice.Keyboard);
		stack.Push(recorder);   // bottom — the game-facing observer
		stack.Push(blocker);    // top — a UI layer that can engage a capture

		// Frame 1: the player presses A. Nothing blocks; the lower layer sees the press.
		stack.Resolve([InputEvent.KeyDown(Key.A)]);
		recorder.Down.ShouldBeTrue();
		recorder.Pressed.ShouldBeTrue();

		// Frame 2: A is still physically held (no event), and the block engages this frame.
		// The lower layer must observe a synthesized Released edge, not keep a stuck key.
		blocker.Blocking = true;
		stack.Resolve([]);
		recorder.Down.ShouldBeFalse();
		recorder.Released.ShouldBeTrue();

		// Frame 3: block sustained — nothing held, no phantom edges.
		stack.Resolve([]);
		recorder.Down.ShouldBeFalse();
		recorder.Pressed.ShouldBeFalse();
		recorder.Released.ShouldBeFalse();

		// Frame 4: block lifts while A is still physically held. The lower layer must observe a
		// synthesized Pressed edge and hold the key again.
		blocker.Blocking = false;
		stack.Resolve([]);
		recorder.Down.ShouldBeTrue();
		recorder.Pressed.ShouldBeTrue();
	}

	[Test]
	public void A_release_for_a_never_pressed_input_is_a_graceful_no_op()
	{
		var stack = new InputLayerStack();
		var recorder = new KeyRecorder(Key.A);
		var blocker = new ToggleBlocker(InputDevice.Keyboard);
		stack.Push(recorder);
		stack.Push(blocker);

		// Engage the block with nothing held: no releases synthesized, no phantom edges downstream.
		blocker.Blocking = true;
		stack.Resolve([]);
		recorder.Down.ShouldBeFalse();
		recorder.Released.ShouldBeFalse();
		recorder.Pressed.ShouldBeFalse();
	}

	[Test]
	public void A_scripted_layer_drives_the_game_while_a_pause_layer_above_still_sees_escape()
	{
		var gameplay = InputActions.Define(b =>
		{
			b.Vector("move", Composite.Wasd);
			b.Button("jump", Key.Space);
		});
		var move = gameplay.Vector("move");

		var stack = new InputLayerStack();
		stack.Push(new ActionMapLayer(gameplay)); // bottom — the game
		stack.Push(new ScriptedLayer());          // middle — swallows the real player, injects scripted input
		var pause = new EscapeWatcher();
		stack.Push(pause);                         // top — a modal that sits above the scripted block

		// The real player presses Escape and W; the scripted layer injects D.
		stack.Resolve([InputEvent.KeyDown(Key.Escape), InputEvent.KeyDown(Key.W)]);

		// The pause layer, above the scripted block, still saw Escape.
		pause.SawEscape.ShouldBeTrue();

		// The game resolved movement from the injected D (right), and never saw the real W (up).
		var moved = gameplay.Value(move);
		moved.X.ShouldBe(1f, 0.0001);
		moved.Y.ShouldBe(0f, 0.0001);
	}

	[Test]
	public void Remove_pulls_a_layer_from_the_middle_of_the_stack()
	{
		var stack = new InputLayerStack();
		var bottom = new KeyRecorder(Key.A);
		var middle = new DeviceBlocker(InputDevice.Keyboard);
		var top = new PassthroughLayer();
		stack.Push(bottom);
		stack.Push(middle);
		stack.Push(top);

		stack.Resolve([InputEvent.KeyDown(Key.A)]);
		bottom.Down.ShouldBeFalse(); // middle blocks the keyboard

		stack.Remove(middle).ShouldBeTrue();
		stack.Contains(middle).ShouldBeFalse();

		stack.Resolve([InputEvent.KeyDown(Key.A)]);
		bottom.Down.ShouldBeTrue(); // with the blocker gone, the key flows through
	}

	[Test]
	public void A_text_input_request_holds_for_the_frames_a_layer_makes_it_and_lapses_after()
	{
		var stack = new InputLayerStack();
		var field = new TextField();
		stack.Push(new PassthroughLayer());
		stack.Push(field);

		stack.Resolve([]);
		stack.TextInputRequested.ShouldBeFalse();

		field.Focused = true;
		stack.Resolve([]);
		stack.TextInputRequested.ShouldBeTrue();
		stack.Resolve([]);
		stack.TextInputRequested.ShouldBeTrue();

		field.Focused = false;
		stack.Resolve([]);
		stack.TextInputRequested.ShouldBeFalse();
	}

	// A layer standing for a text field: it asks for text input while it is focused.
	private sealed class TextField : IInputLayer
	{
		public bool Focused { get; set; }

		public void Process(ref InputContext ctx)
		{
			if (Focused)
				ctx.RequestTextInput();
		}
	}

#if !DEBUG
	[Test]
	public void The_steady_resolve_path_allocates_nothing()
	{
		var stack = new InputLayerStack();
		stack.Push(new KeyRecorder(Key.A));
		stack.Push(new PassthroughLayer());

		var events = new[] { InputEvent.KeyDown(Key.A), InputEvent.PointerMove(5f, 5f) };

		// Warm up: JIT the path and let the top→bottom order buffer reach its size.
		for (var i = 0; i < 200; i++)
			stack.Resolve(events);

		Allocations.ShouldAllocateNothing(() =>
		{
			for (var i = 0; i < 1000; i++)
				stack.Resolve(events);
		});
	}
#endif

	private sealed class KeyRecorder(Key key) : IInputLayer
	{
		public bool Down { get; private set; }

		public bool Pressed { get; private set; }

		public bool Released { get; private set; }

		public void Process(ref InputContext ctx)
		{
			Down = ctx.Snapshot.Down(key);
			Pressed = ctx.Snapshot.Pressed(key);
			Released = ctx.Snapshot.Released(key);
		}
	}

	private sealed class DeviceBlocker(InputDevice devices) : IInputLayer
	{
		public void Process(ref InputContext ctx) => ctx.Block(devices);
	}

	private sealed class ToggleBlocker(InputDevice devices) : IInputLayer
	{
		public bool Blocking { get; set; }

		public void Process(ref InputContext ctx)
		{
			if (Blocking)
				ctx.Block(devices);
		}
	}

	private sealed class Injector(InputEvent injected) : IInputLayer
	{
		public void Process(ref InputContext ctx) => ctx.Inject(injected);
	}

	private sealed class ScriptedLayer : IInputLayer
	{
		public void Process(ref InputContext ctx)
		{
			ctx.Block(InputDevice.Keyboard | InputDevice.Mouse);
			ctx.Inject(InputEvent.KeyDown(Key.D));
		}
	}

	private sealed class EscapeWatcher : IInputLayer
	{
		public bool SawEscape { get; private set; }

		public void Process(ref InputContext ctx) => SawEscape = ctx.Snapshot.Pressed(Key.Escape);
	}

	private sealed class PassthroughLayer : IInputLayer
	{
		public void Process(ref InputContext ctx)
		{
		}
	}
}
