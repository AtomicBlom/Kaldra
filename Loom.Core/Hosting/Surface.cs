namespace Loom.Core.Hosting;

/// <summary>
/// An offscreen render target request: pass to <see cref="IRenderBackend.Create"/> in place of a window surface
/// to bring an engine up with no window/surface, rendering into a readable owned image instead of a swapchain
/// (for CI screenshot regression, off-screen tooling, and software-rasterizer runs). <see cref="IEngine.RequestCapture"/>
/// extracts its pixels exactly as it does for a windowed frame.
/// </summary>
public sealed class OffscreenSurface
{
	/// <summary>The render target's width in pixels.</summary>
	public int Width { get; }

	/// <summary>The render target's height in pixels.</summary>
	public int Height { get; }

	public OffscreenSurface(int width, int height)
	{
		if (width <= 0 || height <= 0)
			throw new ArgumentOutOfRangeException(nameof(width), $"An offscreen surface must have positive dimensions; got {width}x{height}.");
		Width = width;
		Height = height;
	}
}

/// <summary>Factory for a windowless render target — see <see cref="OffscreenSurface"/>.</summary>
public static class Surface
{
	/// <summary>Build an <see cref="OffscreenSurface"/> request sized to <paramref name="width"/>×<paramref name="height"/>.</summary>
	public static OffscreenSurface Offscreen(int width, int height) => new(width, height);
}
