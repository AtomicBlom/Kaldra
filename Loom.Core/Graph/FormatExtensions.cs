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

		/// <summary>
		/// Whether a shader can read this format as a filterable float texture on every backend — what a technique
		/// that declares a plain <c>Texture2D</c> and a filtering sampler (a draw list's) can bind. Depth and integer
		/// formats sample through different shader types, and WebGPU does not filter <see cref="Format.R32F"/> without
		/// an optional feature, so those three are false.
		/// </summary>
		/// <exception cref="NotSupportedException">A format with no entry, which can only be a newly added one.</exception>
		public bool IsFilterableFloat => format switch
		{
			Format.Rgba8Srgb or Format.Rgba8Unorm or Format.Bgra8Srgb or Format.Rgb10A2 => true,
			Format.Rgba16F or Format.R16F or Format.Rg16F => true,
			Format.R32F or Format.R32Uint or Format.Depth32F => false,
			_ => throw new NotSupportedException(
				$"Format {format} has no sampling class. Add it to FormatExtensions.IsFilterableFloat rather than to a backend."),
		};
	}
}
