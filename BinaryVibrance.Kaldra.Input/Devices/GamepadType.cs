using System.Globalization;
using System.Text.RegularExpressions;

namespace BinaryVibrance.Kaldra.Input;

/// <summary>
/// What a connected gamepad is, as far as the host can tell, for the prompts that name its buttons. Buttons themselves
/// stay named by position (<see cref="GamepadButton"/>); this says what is printed on them. Follows SDL's own list.
/// </summary>
public enum GamepadType
{
	/// <summary>Not known: a generic pad, or a host that can't say. Prompts treat it as an Xbox layout.</summary>
	Unknown = 0,
	Xbox360,
	/// <summary>Xbox One and Xbox Series pads (the View and Menu buttons, a Share button on Series pads).</summary>
	XboxOne,
	PS3,
	PS4,
	/// <summary>The DualSense and DualSense Edge.</summary>
	PS5,
	SwitchPro,
	JoyConLeft,
	JoyConRight,
	JoyConPair,
	GameCube,
	/// <summary>Valve's Steam Controller or Steam Deck, read as itself (not Steam's virtual Xbox pad).</summary>
	Steam,
}

/// <summary>Families of <see cref="GamepadType"/> that share their face buttons' labels.</summary>
public enum GamepadFamily
{
	/// <summary>A, B, X, Y as an Xbox pad has them (South is A); also every pad not otherwise known.</summary>
	Xbox,

	/// <summary>Cross, Circle, Square, Triangle (South is Cross).</summary>
	PlayStation,

	/// <summary>B, A, Y, X by position: South is B, East is A.</summary>
	Nintendo,

	/// <summary>A, B, X, Y as an Xbox pad has them, with grips and trackpads besides.</summary>
	Steam,
}

/// <summary>
/// Telling what a pad is from what a host knows of it: its USB ids, a mapping's name, or a browser's <c>Gamepad.id</c>.
/// </summary>
public static partial class GamepadIdentity
{
	public const ushort Sony = 0x054C, Microsoft = 0x045E, Nintendo = 0x057E, Valve = 0x28DE;

	/// <summary>The family whose labels the pad's face buttons carry.</summary>
	public static GamepadFamily Family(this GamepadType type) => type switch
	{
		GamepadType.PS3 or GamepadType.PS4 or GamepadType.PS5 => GamepadFamily.PlayStation,
		GamepadType.SwitchPro or GamepadType.JoyConLeft or GamepadType.JoyConRight or GamepadType.JoyConPair
			or GamepadType.GameCube => GamepadFamily.Nintendo,
		GamepadType.Steam => GamepadFamily.Steam,
		_ => GamepadFamily.Xbox,
	};

	/// <summary>A pad's type from its USB vendor and product ids, or <see cref="GamepadType.Unknown"/>.</summary>
	public static GamepadType FromIds(ushort vendor, ushort product) => vendor switch
	{
		Sony => product switch
		{
			0x0268 => GamepadType.PS3,
			0x05C4 or 0x09CC or 0x0BA0 => GamepadType.PS4,
			0x0CE6 or 0x0DF2 => GamepadType.PS5,
			_ => GamepadType.Unknown,
		},
		Microsoft => product switch
		{
			0x028E or 0x028F or 0x0291 or 0x02A1 or 0x0719 => GamepadType.Xbox360,
			_ => GamepadType.XboxOne,
		},
		Nintendo => product switch
		{
			0x2006 => GamepadType.JoyConLeft,
			0x2007 => GamepadType.JoyConRight,
			0x2008 or 0x200E => GamepadType.JoyConPair,
			0x2009 => GamepadType.SwitchPro,
			0x0337 => GamepadType.GameCube,
			_ => GamepadType.Unknown,
		},
		// Steam's own virtual pad (11FF) is an Xbox 360 pad as far as its buttons go
		Valve => product == 0x11FF ? GamepadType.Xbox360 : GamepadType.Steam,
		_ => GamepadType.Unknown,
	};

	/// <summary>A pad's type from its name (a GLFW mapping's, a browser's), or <see cref="GamepadType.Unknown"/>.</summary>
	public static GamepadType FromName(string? name)
	{
		if (string.IsNullOrEmpty(name))
			return GamepadType.Unknown;
		bool Has(string s) => name.Contains(s, StringComparison.OrdinalIgnoreCase);
		if (Has("DualSense") || Has("PS5"))
			return GamepadType.PS5;
		if (Has("DualShock 4") || Has("PS4") || name.Equals("Wireless Controller", StringComparison.OrdinalIgnoreCase))
			return GamepadType.PS4;
		if (Has("PS3") || Has("PLAYSTATION(R)3"))
			return GamepadType.PS3;
		if (Has("Steam Virtual") || Has("Xbox 360"))
			return GamepadType.Xbox360;
		if (Has("Xbox"))
			return GamepadType.XboxOne;
		if (Has("Steam Controller") || Has("Steam Deck"))
			return GamepadType.Steam;
		if (Has("Pro Controller"))
			return GamepadType.SwitchPro;
		if (Has("Joy-Con (L)"))
			return GamepadType.JoyConLeft;
		if (Has("Joy-Con (R)"))
			return GamepadType.JoyConRight;
		if (Has("Joy-Con"))
			return GamepadType.JoyConPair;
		if (Has("GameCube"))
			return GamepadType.GameCube;
		return GamepadType.Unknown;
	}

	/// <summary>
	/// What a browser's <c>Gamepad.id</c> says: Chromium writes <c>Name (STANDARD GAMEPAD Vendor: 054c Product: 0ce6)</c>,
	/// Firefox <c>054c-0ce6-Name</c>, Safari the name alone. The ids where it gives them, the type from them, else from
	/// the name.
	/// </summary>
	public static GamepadType FromBrowserId(string? id, out ushort vendor, out ushort product)
	{
		vendor = product = 0;
		if (string.IsNullOrEmpty(id))
			return GamepadType.Unknown;
		var name = id;
		if (Chromium().Match(id) is { Success: true } c)
		{
			vendor = Hex(c.Groups[1].Value);
			product = Hex(c.Groups[2].Value);
			name = id[..c.Index].Trim();
		}
		else if (Firefox().Match(id) is { Success: true } f)
		{
			vendor = Hex(f.Groups[1].Value);
			product = Hex(f.Groups[2].Value);
			name = id[f.Length..];
		}
		var type = FromIds(vendor, product);
		return type != GamepadType.Unknown ? type : FromName(name);
	}

	private static ushort Hex(string s) => ushort.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

	[GeneratedRegex(@"\(.*Vendor:\s*([0-9a-fA-F]{4})\s+Product:\s*([0-9a-fA-F]{4})\s*\)")]
	private static partial Regex Chromium();

	[GeneratedRegex(@"^([0-9a-fA-F]{1,4})-([0-9a-fA-F]{1,4})-")]
	private static partial Regex Firefox();
}
