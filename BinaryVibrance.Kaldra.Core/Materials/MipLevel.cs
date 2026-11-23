namespace BinaryVibrance.Kaldra.Core.Materials;

/// <summary>
/// One level of a texture's mip chain: tightly packed RGBA8 (row pitch = <c>Width*4</c>, no padding). The
/// backend re-pads to its own row-pitch rules on upload.
/// </summary>
/// <param name="Width">Level width in texels.</param>
/// <param name="Height">Level height in texels.</param>
/// <param name="Pixels">RGBA8 texels, row-major, tightly packed (length == <c>Width*Height*4</c>).</param>
public readonly record struct MipLevel(int Width, int Height, byte[] Pixels);
