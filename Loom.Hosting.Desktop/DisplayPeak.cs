using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.DXGI;

namespace Loom.Hosting.Desktop;

/// <summary>
/// The brightest a window's monitor shows, in nits, as the display reports it to DXGI (<c>DXGI_OUTPUT_DESC1.MaxLuminance</c>,
/// from its EDID): what an HDR frame's highlights should roll off toward rather than clip at. Asked of the output whose
/// monitor the window is on, through any adapter, so it serves a Vulkan frame as well as a DirectX 12 one. Null off
/// Windows, where the output reports none, or where any step fails.
/// </summary>
internal static unsafe class DisplayPeak
{
	public static float? Nits(nint hwnd)
	{
		if (!OperatingSystem.IsWindows() || hwnd == 0)
			return null;
		try
		{
			var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
			using var dxgi = DXGI.GetApi(default(INativeWindowSource));
			if (dxgi.CreateDXGIFactory1(out ComPtr<IDXGIFactory1> factory) < 0)
				return null;
			using (factory)
			{
				ComPtr<IDXGIAdapter1> adapter = default;
				for (uint a = 0; factory.EnumAdapters1(a, ref adapter) >= 0; a++)
				{
					using (adapter)
					{
						ComPtr<IDXGIOutput> output = default;
						for (uint o = 0; adapter.EnumOutputs(o, ref output) >= 0; o++)
						{
							using (output)
							{
								if (output.QueryInterface(out ComPtr<IDXGIOutput6> output6) < 0)
									continue;
								using (output6)
								{
									OutputDesc1 desc = default;
									if (output6.GetDesc1(ref desc) < 0 || desc.Monitor != monitor)
										continue;
									return desc.MaxLuminance > 0f ? desc.MaxLuminance : null;
								}
							}
						}
					}
				}
			}
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
		{
		}
		return null;
	}

	[DllImport("user32.dll")]
	private static extern nint MonitorFromWindow(nint hwnd, uint flags);
}
