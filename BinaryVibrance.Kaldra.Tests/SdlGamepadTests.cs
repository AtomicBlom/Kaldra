using System.Runtime.InteropServices;

using BinaryVibrance.Kaldra.Hosting.Desktop;
using BinaryVibrance.Kaldra.Hosting.Desktop.Sdl;
using BinaryVibrance.Kaldra.Input;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// The desktop host's SDL 3 gamepads (<see cref="SdlGamepadPump"/>): the event struct laid out as SDL's, its buttons,
/// axes and levels translated to Kaldra's, the <c>--pads</c> option, and the shipped SDL3.dll being the pinned release.
/// None of it needs a pad; the pump against real pads is checked by hand.
/// </summary>
public class SdlGamepadTests
{
	[Test]
	public void The_event_is_laid_out_as_SDL_lays_out_its_gamepad_events()
	{
		Marshal.SizeOf<SdlEvent>().ShouldBe(128);
		Marshal.OffsetOf<SdlEvent>(nameof(SdlEvent.Type)).ToInt32().ShouldBe(0);
		Marshal.OffsetOf<SdlEvent>(nameof(SdlEvent.Which)).ToInt32().ShouldBe(16);
		Marshal.OffsetOf<SdlEvent>(nameof(SdlEvent.Button)).ToInt32().ShouldBe(20);
		Marshal.OffsetOf<SdlEvent>(nameof(SdlEvent.Down)).ToInt32().ShouldBe(21);
		Marshal.OffsetOf<SdlEvent>(nameof(SdlEvent.Axis)).ToInt32().ShouldBe(20);
		Marshal.OffsetOf<SdlEvent>(nameof(SdlEvent.Value)).ToInt32().ShouldBe(24);
	}

	[Test]
	public void Buttons_are_named_by_position_and_none_twice()
	{
		SdlGamepadMap.Button(0).ShouldBe(GamepadButton.South);
		SdlGamepadMap.Button(1).ShouldBe(GamepadButton.East);
		SdlGamepadMap.Button(2).ShouldBe(GamepadButton.West);
		SdlGamepadMap.Button(3).ShouldBe(GamepadButton.North);
		SdlGamepadMap.Button(9).ShouldBe(GamepadButton.LeftBumper);
		SdlGamepadMap.Button(16).ShouldBe(GamepadButton.RightPaddle1);
		SdlGamepadMap.Button(17).ShouldBe(GamepadButton.LeftPaddle1);
		SdlGamepadMap.Button(20).ShouldBe(GamepadButton.Touchpad);

		var mapped = Enumerable.Range(0, 21).Select(b => SdlGamepadMap.Button(b)).ToArray();
		mapped.ShouldAllBe(b => b != null);
		mapped.Distinct().Count().ShouldBe(21);
		mapped.ShouldAllBe(b => (int)b!.Value < 32);   // a bit in the snapshot's mask

		for (int b = 21; b < 26; b++)
			SdlGamepadMap.Button(b).ShouldBeNull();   // SDL's MISC2..6: nothing Kaldra names
	}

	[Test]
	public void Sticks_read_from_minus_one_to_one_and_triggers_from_zero_to_one()
	{
		SdlGamepadMap.Level(0, short.MinValue).ShouldBe(-1f);
		SdlGamepadMap.Level(0, short.MaxValue).ShouldBe(1f);
		SdlGamepadMap.Level(1, 0).ShouldBe(0f);
		SdlGamepadMap.Level(3, -16384).ShouldBe(-0.5f, 0.001f);

		SdlGamepadMap.Level(4, 0).ShouldBe(0f);
		SdlGamepadMap.Level(5, short.MaxValue).ShouldBe(1f);
		SdlGamepadMap.Level(5, -5).ShouldBe(0f);

		SdlGamepadMap.Axis(4).ShouldBe(GamepadAxis.LeftTrigger);
		SdlGamepadMap.Axis(5).ShouldBe(GamepadAxis.RightTrigger);
		SdlGamepadMap.Axis(6).ShouldBeNull();
	}

	[Test]
	public void Motor_strength_spans_SDLs_range()
	{
		SdlGamepadMap.Motor(0f).ShouldBe((ushort)0);
		SdlGamepadMap.Motor(1f).ShouldBe(ushort.MaxValue);
		SdlGamepadMap.Motor(2f).ShouldBe(ushort.MaxValue);
		SdlGamepadMap.Motor(-1f).ShouldBe((ushort)0);
		SdlGamepadMap.Motor(0.5f).ShouldBe((ushort)32768);
	}

	[Test]
	public void Pads_default_to_SDL_and_can_be_read_through_GLFW()
	{
		HostOptions.Parse([]).Pads.ShouldBe(GamepadSource.Sdl);
		HostOptions.Parse(["--pads", "glfw"]).Pads.ShouldBe(GamepadSource.Glfw);
		HostOptions.Parse(["--pads=SDL"]).Pads.ShouldBe(GamepadSource.Sdl);
		HostOptions.Parse(["--pads", "xinput"]).ShowHelp.ShouldBeTrue();
	}

	[Test]
	public void The_shipped_SDL_is_the_pinned_release()
	{
		if (!OperatingSystem.IsWindows())
			return;   // SDL is fetched for Windows only; elsewhere pads come through GLFW
		Sdl3.TryLoad(out var reason).ShouldBeTrue(reason);
		int v = Sdl3.SDL_GetVersion();
		$"{v / 1000000}.{v / 1000 % 1000}.{v % 1000}".ShouldBe("3.4.20");   // BinaryVibrance.Kaldra.SDL3's version, in Directory.Packages.props
	}
}
