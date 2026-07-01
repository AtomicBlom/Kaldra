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

	/// <summary>
	/// The byte alignment one attribute of this format requires — its natural scalar-component alignment
	/// (a float/uint attribute wants 4, a half attribute 2, a packed byte word 1). A <see cref="VertexLayout"/>
	/// rounds each attribute's offset up to this so no attribute straddles its own alignment boundary, and rounds
	/// the stride up to the layout's widest alignment. Every current format is 4-byte-granular, so the rounding is
	/// a no-op today; the helper keeps a future sub-4-byte or wider-aligned format packing correctly.
	/// </summary>
	public static int Alignment(this VertexFormat format) => format switch
	{
		VertexFormat.Float => sizeof(float),
		VertexFormat.Float2 => sizeof(float),
		VertexFormat.Float3 => sizeof(float),
		VertexFormat.Float4 => sizeof(float),
		VertexFormat.UInt4 => sizeof(uint),
		VertexFormat.Half4 => sizeof(ushort),
		VertexFormat.UNorm4 => sizeof(byte), // one packed 32-bit word, but its scalar granularity is a byte
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
