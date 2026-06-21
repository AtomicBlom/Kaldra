namespace Loom.Core.Geometry;

/// <summary>Helpers over <see cref="VertexFormat"/> — kept here so the enum stays a plain value list.</summary>
public static class VertexFormatExtensions
{
	/// <summary>The size of one attribute of this format, in bytes — drives the packed offsets/stride of a <see cref="VertexLayout"/>.</summary>
	public static int ByteSize(this VertexFormat format) => format switch
	{
		VertexFormat.Float => sizeof(float),
		VertexFormat.Float2 => 2 * sizeof(float),
		VertexFormat.Float3 => 3 * sizeof(float),
		VertexFormat.Float4 => 4 * sizeof(float),
		VertexFormat.UInt4 => 4 * sizeof(uint),
		VertexFormat.Half4 => 4 * sizeof(ushort),
		VertexFormat.UNorm4 => 4 * sizeof(byte),
		_ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown vertex format."),
	};

	/// <summary>The number of 32-bit components — useful for the backends' attribute-description builders.</summary>
	public static int ComponentCount(this VertexFormat format) => format switch
	{
		VertexFormat.Float => 1,
		VertexFormat.Float2 => 2,
		VertexFormat.Float3 => 3,
		VertexFormat.Float4 => 4,
		VertexFormat.UInt4 => 4,
		VertexFormat.Half4 => 4,
		VertexFormat.UNorm4 => 1, // one packed 32-bit word, unpacked to four channels by the input assembler
		_ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown vertex format."),
	};
}
