using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.DXGI;

namespace BinaryVibrance.Kaldra.Hosting.Desktop;

/// <summary>
/// What a window's monitor reports to DXGI (<c>DXGI_OUTPUT_DESC1</c>) about itself: whether Windows runs it in HDR, and
/// the brightest it shows. Asked of the output whose monitor the window is on, through any adapter, so it serves a
/// Vulkan frame as well as a DirectX 12 one. Null off Windows, or where any step fails.
/// </summary>
internal static class DisplayOutput
{
	/// <summary>
	/// Whether the window's monitor is in HDR (Windows' "Use HDR" switched on): its output's colour space is HDR10's
	/// (G2084, P2020). An scRGB swapchain presents on an SDR monitor too, composed down and clipped past paper white, so
	/// a surface able to present scRGB says nothing about the monitor; this does.
	/// </summary>
	public static bool? IsHdr(nint hwnd) =>
		Describe(hwnd) is { } desc ? desc.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020 : null;

	/// <summary>
	/// The brightest the window's monitor shows, in nits (<c>MaxLuminance</c>, from its EDID): what an HDR frame's
	/// highlights should roll off toward rather than clip at. Null where the monitor reports none.
	/// </summary>
	public static float? PeakNits(nint hwnd) =>
		Describe(hwnd) is { MaxLuminance: > 0f } desc ? desc.MaxLuminance : null;

	// The window's monitor's output description, asked through a factory made now so its outputs are current
	private static OutputDesc1? Describe(nint hwnd)
	{
		if (!OperatingSystem.IsWindows() || hwnd == 0)
			return null;
		try
		{
			var monitor = User32.MonitorFromWindow(hwnd, User32.MonitorDefaultToNearest);
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
									return desc;
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
}
