using Loom.Core.Hosting;

using SUIsei;

namespace Loom.UI.SUIsei;

/// <summary>The host's <see cref="ScreenInsets"/> in SUIsei's terms, for <see cref="UIRoot.SafeAreaInsets"/>.</summary>
public static class ScreenInsetsExtensions
{
	extension(ScreenInsets insets)
	{
		/// <summary>The same insets as a SUIsei <see cref="Thickness"/>, still in surface pixels.</summary>
		public Thickness ToThickness() => new(insets.Left, insets.Top, insets.Right, insets.Bottom);
	}
}
