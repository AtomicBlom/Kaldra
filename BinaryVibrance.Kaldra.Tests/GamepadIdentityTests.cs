using BinaryVibrance.Kaldra.Hosting.Desktop.Sdl;
using BinaryVibrance.Kaldra.Input;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// What a pad is (<see cref="GamepadType"/>): from USB ids, a mapping's name and a browser's <c>Gamepad.id</c>
/// (<see cref="GamepadIdentity"/>), from SDL's own type, and carried by the connect event into the snapshot.
/// </summary>
public class GamepadIdentityTests
{
	[Test]
	[Arguments("DualSense Wireless Controller (STANDARD GAMEPAD Vendor: 054c Product: 0ce6)", GamepadType.PS5, 0x054C, 0x0CE6)]
	[Arguments("Wireless Controller (STANDARD GAMEPAD Vendor: 054c Product: 09cc)", GamepadType.PS4, 0x054C, 0x09CC)]
	[Arguments("Xbox 360 Controller (XInput STANDARD GAMEPAD)", GamepadType.Xbox360, 0, 0)]
	[Arguments("Xbox Wireless Controller (STANDARD GAMEPAD Vendor: 045e Product: 0b13)", GamepadType.XboxOne, 0x045E, 0x0B13)]
	[Arguments("Pro Controller (STANDARD GAMEPAD Vendor: 057e Product: 2009)", GamepadType.SwitchPro, 0x057E, 0x2009)]
	[Arguments("054c-0ce6-DualSense Wireless Controller", GamepadType.PS5, 0x054C, 0x0CE6)]
	[Arguments("45e-b13-Xbox Wireless Controller", GamepadType.XboxOne, 0x045E, 0x0B13)]
	[Arguments("DualSense Wireless Controller", GamepadType.PS5, 0, 0)]                                     // Safari: the name alone
	[Arguments("Unknown Gamepad (Vendor: 1234 Product: abcd)", GamepadType.Unknown, 0x1234, 0xABCD)]
	[Arguments("", GamepadType.Unknown, 0, 0)]
	public void A_browsers_id_says_what_the_pad_is(string id, GamepadType expected, int vendor, int product)
	{
		GamepadIdentity.FromBrowserId(id, out var v, out var p).ShouldBe(expected);
		v.ShouldBe((ushort)vendor);
		p.ShouldBe((ushort)product);
	}

	[Test]
	[Arguments("PS5 Controller", GamepadType.PS5)]                       // GLFW's mapping names
	[Arguments("PS4 Controller", GamepadType.PS4)]
	[Arguments("Xbox Controller", GamepadType.XboxOne)]
	[Arguments("Steam Virtual Gamepad", GamepadType.Xbox360)]
	[Arguments("Steam Controller", GamepadType.Steam)]
	[Arguments("Nintendo Switch Pro Controller", GamepadType.SwitchPro)]
	[Arguments("Some Pad", GamepadType.Unknown)]
	public void A_name_says_what_the_pad_is(string name, GamepadType expected)
		=> GamepadIdentity.FromName(name).ShouldBe(expected);

	[Test]
	public void Families_carry_their_face_labels()
	{
		GamepadType.PS5.Family().ShouldBe(GamepadFamily.PlayStation);
		GamepadType.XboxOne.Family().ShouldBe(GamepadFamily.Xbox);
		GamepadType.Unknown.Family().ShouldBe(GamepadFamily.Xbox);
		GamepadType.SwitchPro.Family().ShouldBe(GamepadFamily.Nintendo);
		GamepadType.Steam.Family().ShouldBe(GamepadFamily.Steam);
		GamepadIdentity.FromIds(GamepadIdentity.Valve, 0x11FF).ShouldBe(GamepadType.Xbox360);   // Steam's virtual pad
		GamepadIdentity.FromIds(GamepadIdentity.Valve, 0x1102).ShouldBe(GamepadType.Steam);
	}

	[Test]
	public void SDLs_types_are_Kaldras()
	{
		SdlGamepadMap.Type(1).ShouldBe(GamepadType.Unknown);   // SDL's "standard": a pad it doesn't know
		SdlGamepadMap.Type(3).ShouldBe(GamepadType.XboxOne);
		SdlGamepadMap.Type(6).ShouldBe(GamepadType.PS5);
		SdlGamepadMap.Type(12).ShouldBe(GamepadType.Steam);
		SdlGamepadMap.Type(99).ShouldBe(GamepadType.Unknown);
	}

	[Test]
	public void The_snapshot_keeps_what_a_pad_is_while_it_is_connected()
	{
		var snapshot = new InputSnapshot();
		snapshot.Advance([InputEvent.GamepadConnected(1, GamepadType.PS5, 0x054C, 0x0CE6)]);
		var pad = snapshot.Gamepad(1);
		pad.Connected.ShouldBeTrue();
		pad.Type.ShouldBe(GamepadType.PS5);
		pad.Family.ShouldBe(GamepadFamily.PlayStation);
		pad.Vendor.ShouldBe((ushort)0x054C);
		pad.Product.ShouldBe((ushort)0x0CE6);

		snapshot.Advance([]);
		snapshot.Gamepad(1).Type.ShouldBe(GamepadType.PS5);

		snapshot.Advance([InputEvent.GamepadDisconnected(1)]);
		snapshot.Gamepad(1).Type.ShouldBe(GamepadType.Unknown);
		snapshot.Gamepad(1).Vendor.ShouldBe((ushort)0);

		snapshot.Advance([InputEvent.GamepadConnected(1)]);   // a host that can't say
		snapshot.Gamepad(1).Type.ShouldBe(GamepadType.Unknown);
	}
}
