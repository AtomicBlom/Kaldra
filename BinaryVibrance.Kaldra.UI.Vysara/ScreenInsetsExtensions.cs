using BinaryVibrance.Kaldra.Core.Hosting;

using BinaryVibrance.Vysara;

namespace BinaryVibrance.Kaldra.UI.Vysara;

/// <summary>The host's <see cref="ScreenInsets"/> in Vysara's terms, for <see cref="UIRoot.SafeAreaInsets"/>.</summary>
public static class ScreenInsetsExtensions
{
	extension(ScreenInsets insets)
	{
		/// <summary>The same insets as a Vysara <see cref="Thickness"/>, still in surface pixels.</summary>
		public Thickness ToThickness() => new(insets.Left, insets.Top, insets.Right, insets.Bottom);
	}
}
