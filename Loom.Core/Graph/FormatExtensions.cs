namespace Loom.Core.Graph;

/// <summary>
/// Neutral facts about a <see cref="Format"/> — the questions every backend needs the same answer to, so they are
/// answered here rather than in each one's own switch.
/// </summary>
public static class FormatExtensions
{
	extension(Format format)
	{
		/// <summary>
		/// Bytes one texel of this format occupies, tightly packed. This is what a readback copy is sized by and what
		/// its rows are unpacked with, and it is the only thing a backend has to know about a format to hand its bytes
		/// back to the app — which is why it lives here: three backends each carrying their own table meant a
		/// <c>g.Readback</c> on a format one of them had listed and another had not worked on one backend and threw on
		/// the other, for no reason to do with the device.
		/// </summary>
		/// <exception cref="NotSupportedException">
		/// A format with no entry, which can only be a newly added one — the point of throwing rather than guessing is
		/// that a wrong size silently reads the wrong bytes back.
		/// </exception>
		public int BytesPerTexel => format switch
		{
			Format.R16F => 2,
			Format.Rg16F or Format.R32F or Format.R32Uint or Format.Depth32F => 4,
			Format.Rgba8Srgb or Format.Rgba8Unorm or Format.Bgra8Srgb or Format.Rgb10A2 => 4,
			Format.Rgba16F => 8,
			_ => throw new NotSupportedException(
				$"Format {format} has no defined texel size. Add it to FormatExtensions.BytesPerTexel rather than to a backend."),
		};
	}
}
