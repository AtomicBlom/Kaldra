using System.Runtime.InteropServices;

namespace Loom.Hosting.Desktop;

/// <summary>
/// The luminance Windows shows SDR white at on the monitor a window is on, when that monitor runs in HDR: the "SDR
/// content brightness" the user chose in Display settings. An HDR frame should put its paper white there, so its UI and
/// its whites match every other window on the desktop rather than sitting dimmer (greyer) or brighter beside them.
/// <para>
/// Read through the display configuration API: the window's monitor's GDI device name, the active display path whose
/// source has that name, and that path's target's <c>DISPLAYCONFIG_SDR_WHITE_LEVEL</c> (in thousandths of 80 nits).
/// Null off Windows, or where any step fails.
/// </para>
/// </summary>
internal static unsafe class SdrWhiteLevel
{
	public static float? Nits(nint hwnd)
	{
		if (!OperatingSystem.IsWindows() || hwnd == 0)
			return null;
		try
		{
			var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
			var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
			if (monitor == 0 || !GetMonitorInfoW(monitor, &info))
				return null;
			var device = new string(info.Device);

			if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0)
				return null;
			var paths = new byte[pathCount * PathInfoSize];
			var modes = new byte[modeCount * ModeInfoSize];
			fixed (byte* p = paths, m = modes)
			{
				if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, p, ref modeCount, m, 0) != 0)
					return null;
				for (var i = 0; i < pathCount; i++)
				{
					var path = p + i * PathInfoSize;
					// DISPLAYCONFIG_PATH_INFO: sourceInfo { LUID adapterId; UINT32 id; ... } at 0, targetInfo { LUID; id } at 20
					var source = new SourceName { Header = Header(TypeGetSourceName, sizeof(SourceName), path, 0) };
					if (DisplayConfigGetDeviceInfo(&source.Header) != 0 || new string(source.ViewGdiDeviceName) != device)
						continue;
					var white = new WhiteLevel { Header = Header(TypeGetSdrWhiteLevel, sizeof(WhiteLevel), path, 20) };
					if (DisplayConfigGetDeviceInfo(&white.Header) != 0)
						return null;
					return white.Level / 1000f * 80f;
				}
			}
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
		}
		return null;
	}

	private static DeviceInfoHeader Header(int type, int size, byte* path, int at) => new()
	{
		Type = type,
		Size = (uint)size,
		AdapterLow = *(uint*)(path + at),
		AdapterHigh = *(int*)(path + at + 4),
		Id = *(uint*)(path + at + 8),
	};

	private const uint MonitorDefaultToNearest = 2;
	private const uint QdcOnlyActivePaths = 2;
	private const int PathInfoSize = 72;   // sizeof(DISPLAYCONFIG_PATH_INFO)
	private const int ModeInfoSize = 64;   // sizeof(DISPLAYCONFIG_MODE_INFO)
	private const int TypeGetSourceName = 1;      // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
	private const int TypeGetSdrWhiteLevel = 11;  // DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL

	[StructLayout(LayoutKind.Sequential)]
	private struct MonitorInfoEx
	{
		public uint Size;
		public int MonitorLeft, MonitorTop, MonitorRight, MonitorBottom;
		public int WorkLeft, WorkTop, WorkRight, WorkBottom;
		public uint Flags;
		public fixed char Device[32];
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DeviceInfoHeader
	{
		public int Type;
		public uint Size;
		public uint AdapterLow;
		public int AdapterHigh;
		public uint Id;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SourceName
	{
		public DeviceInfoHeader Header;
		public fixed char ViewGdiDeviceName[32];
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct WhiteLevel
	{
		public DeviceInfoHeader Header;
		public uint Level;
	}

	[DllImport("user32.dll")]
	private static extern nint MonitorFromWindow(nint hwnd, uint flags);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetMonitorInfoW(nint monitor, MonitorInfoEx* info);

	[DllImport("user32.dll")]
	private static extern int GetDisplayConfigBufferSizes(uint flags, out int pathCount, out int modeCount);

	[DllImport("user32.dll")]
	private static extern int QueryDisplayConfig(uint flags, ref int pathCount, byte* paths, ref int modeCount, byte* modes, nint topologyId);

	[DllImport("user32.dll")]
	private static extern int DisplayConfigGetDeviceInfo(DeviceInfoHeader* request);
}
