using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BinaryVibrance.Kaldra.Hosting.Desktop.Sdl;

/// <summary>
/// The few SDL 3 functions the desktop host reads gamepads through (<see cref="SdlGamepadPump"/>): our own binding, not a
/// package. Every signature is blittable — pointers, integers, SDL's one-byte <c>bool</c> as <see cref="byte"/>, handles
/// as <see cref="nint"/> — so <see cref="LibraryImportAttribute"/>'s generated stubs marshal nothing and call straight
/// through. Getters that only read a field take <see cref="SuppressGCTransitionAttribute"/>; nothing that pumps events,
/// opens a device or rumbles does. Declarations follow SDL 3.4's headers (flibit's SDL3-CS for reference).
/// </summary>
/// <remarks>
/// SDL3.dll comes from the <c>BinaryVibrance.Kaldra.SDL3</c> package: under <c>runtimes/win-&lt;arch&gt;/native/</c>
/// beside a build without a runtime identifier, beside the executable in a publish for one. <see cref="Resolve"/> looks
/// in both before the default probe; <see cref="TryLoad"/> is the one place that asks.
/// </remarks>
internal static unsafe partial class Sdl3
{
	private const string Lib = "SDL3";

	public const uint InitGamepad = 0x00002000;   // SDL_INIT_GAMEPAD: implies SDL_INIT_JOYSTICK and SDL_INIT_EVENTS

	// SDL_EventType: the gamepad events; every other event is drained and dropped
	public const uint EventGamepadAxisMotion = 0x650;
	public const uint EventGamepadButtonDown = 0x651;
	public const uint EventGamepadButtonUp = 0x652;
	public const uint EventGamepadAdded = 0x653;
	public const uint EventGamepadRemoved = 0x654;

	public const int ButtonCount = 26;   // SDL_GAMEPAD_BUTTON_COUNT
	public const int AxisCount = 6;      // SDL_GAMEPAD_AXIS_COUNT

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	public static partial byte SDL_Init(uint flags);

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	public static partial void SDL_Quit();

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	public static partial byte SDL_SetHint(byte* name, byte* value);

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	public static partial byte* SDL_GetError();

	// Not SuppressGCTransition: called from TryCreate, which must run (and fail softly) where SDL3.dll is missing, and a
	// suppressed call may be bound to its library as its caller is compiled
	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	public static partial int SDL_GetVersion();

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	public static partial byte SDL_PollEvent(SdlEvent* e);

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	public static partial nint SDL_OpenGamepad(uint instanceId);

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	public static partial void SDL_CloseGamepad(nint gamepad);

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)]), SuppressGCTransition]
	public static partial byte* SDL_GetGamepadName(nint gamepad);

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)]), SuppressGCTransition]
	public static partial int SDL_GetGamepadType(nint gamepad);

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)]), SuppressGCTransition]
	public static partial int SDL_GetRealGamepadType(nint gamepad);

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)]), SuppressGCTransition]
	public static partial ushort SDL_GetGamepadVendor(nint gamepad);

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)]), SuppressGCTransition]
	public static partial ushort SDL_GetGamepadProduct(nint gamepad);

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)]), SuppressGCTransition]
	public static partial int SDL_GetGamepadConnectionState(nint gamepad);

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)]), SuppressGCTransition]
	public static partial int SDL_GetGamepadButtonLabel(nint gamepad, int button);

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	public static partial byte SDL_GetGamepadButton(nint gamepad, int button);

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	public static partial short SDL_GetGamepadAxis(nint gamepad, int axis);

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	public static partial byte SDL_RumbleGamepad(nint gamepad, ushort lowFrequency, ushort highFrequency, uint durationMs);

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	public static partial void SDL_QuitSubSystem(uint flags);

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	public static partial uint SDL_WasInit(uint flags);

	// The file dialogs (SDL_dialog.h), for SdlFileDialogs. Each returns at once, calling back later, perhaps on another
	// thread; filters and the default location must stay valid until it does
	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	public static partial void SDL_ShowOpenFileDialog(delegate* unmanaged[Cdecl]<void*, byte**, int, void> callback, void* userdata, nint window, DialogFileFilter* filters, int nfilters, byte* defaultLocation, byte allowMany);

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	public static partial void SDL_ShowSaveFileDialog(delegate* unmanaged[Cdecl]<void*, byte**, int, void> callback, void* userdata, nint window, DialogFileFilter* filters, int nfilters, byte* defaultLocation);

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	public static partial void SDL_ShowOpenFolderDialog(delegate* unmanaged[Cdecl]<void*, byte**, int, void> callback, void* userdata, nint window, byte* defaultLocation, byte allowMany);

	[LibraryImport(Lib), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
	public static partial void SDL_PumpEvents();

	/// <summary>SDL_DialogFileFilter: a kind of file's name, and its extensions joined by ';' ("*" for every file).</summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct DialogFileFilter
	{
		public byte* Name;
		public byte* Pattern;
	}

	/// <summary>
	/// Stops the subsystems in <paramref name="flags"/>, and SDL itself once nothing else uses it. SDL counts each
	/// subsystem's starts, so the gamepad pump stops only what it started, and an audio library in the same process using
	/// SDL keeps playing.
	/// </summary>
	public static void QuitSubSystem(uint flags)
	{
		SDL_QuitSubSystem(flags);
		if (SDL_WasInit(0) == 0)
			SDL_Quit();
	}

	/// <summary>A NUL-terminated UTF-8 string of SDL's, as a managed string ("" for null).</summary>
	public static string Text(byte* p) => p == null ? "" : Marshal.PtrToStringUTF8((nint)p) ?? "";

	/// <summary>Sets one of SDL's hints (before <see cref="SDL_Init"/>, for the ones read at init).</summary>
	public static void Hint(string name, string value)
	{
		Span<byte> n = stackalloc byte[name.Length * 3 + 1];
		Span<byte> v = stackalloc byte[value.Length * 3 + 1];
		n[System.Text.Encoding.UTF8.GetBytes(name, n)] = 0;
		v[System.Text.Encoding.UTF8.GetBytes(value, v)] = 0;
		fixed (byte* pn = n, pv = v)
			SDL_SetHint(pn, pv);
	}

	private static int _resolver;   // 1 once the resolver is set: an assembly takes one, once

	/// <summary>
	/// Points this assembly's imports of SDL3 at the DLL beside the app. Called from the assembly's module initializer
	/// (<see cref="DesktopAppHost"/>'s), before any of its code is compiled: a <see cref="SuppressGCTransitionAttribute"/>
	/// call may be bound to its library as its caller is compiled, and bound before the resolver was set, it failed to
	/// find SDL3 in a published build (2026-11-08). Safe to call again.
	/// </summary>
	public static void InstallResolver()
	{
		if (Interlocked.Exchange(ref _resolver, 1) == 0)
			NativeLibrary.SetDllImportResolver(typeof(Sdl3).Assembly, Resolve);
	}

	/// <summary>Whether SDL3.dll is there and loads, with the reason when it isn't.</summary>
	public static bool TryLoad(out string reason)
	{
		InstallResolver();
		if (Find() is not { } handle)
		{
			reason = $"SDL3.dll not found beside the app (looked in {Candidates().FirstOrDefault() ?? AppContext.BaseDirectory})";
			return false;
		}
		NativeLibrary.Free(handle);
		reason = "";
		return true;
	}

	private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
		=> name == Lib && Find() is { } handle ? handle : 0;

	private static nint? Find()
	{
		foreach (var path in Candidates())
			if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
				return handle;
		return NativeLibrary.TryLoad(Lib, typeof(Sdl3).Assembly, null, out var probed) ? probed : null;
	}

	// The architecture's own folder first (an AnyCPU build carries both), then beside the app (a RID-specific publish)
	private static IEnumerable<string> Candidates()
	{
		if (!OperatingSystem.IsWindows())
			yield break;
		var arch = RuntimeInformation.ProcessArchitecture switch
		{
			Architecture.X64 => "x64",
			Architecture.Arm64 => "arm64",
			_ => null,
		};
		if (arch != null)
			yield return Path.Combine(AppContext.BaseDirectory, "runtimes", $"win-{arch}", "native", "SDL3.dll");
		yield return Path.Combine(AppContext.BaseDirectory, "SDL3.dll");
	}
}

/// <summary>
/// <c>SDL_Event</c>: a 128-byte union, of which only the gamepad events' fields are laid out. Every gamepad event starts
/// <c>Uint32 type; Uint32 reserved; Uint64 timestamp; SDL_JoystickID which;</c>, then its own fields at offset 20.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct SdlEvent
{
	[FieldOffset(0)] public uint Type;
	[FieldOffset(16)] public uint Which;     // the joystick's instance id
	[FieldOffset(20)] public byte Axis;      // SDL_GamepadAxisEvent.axis (Uint8), then 3 bytes of padding
	[FieldOffset(20)] public byte Button;    // SDL_GamepadButtonEvent.button (Uint8)
	[FieldOffset(21)] public byte Down;      // SDL_GamepadButtonEvent.down (bool)
	[FieldOffset(24)] public short Value;    // SDL_GamepadAxisEvent.value (Sint16)
}
