namespace BinaryVibrance.Kaldra.Graph;

/// <summary>
/// The rules <see cref="BinaryVibrance.Kaldra.Core.Hosting.IEngine.UpdateTexture"/> holds a texture to, shared so that every backend
/// refuses the same updates with the same words. A backend reads the shape from its own store and asks here before it
/// copies anything.
/// </summary>
public static class TextureUpdates
{
	/// <summary>
	/// Refuse an update the texture cannot take in place: one with a mip chain (the new texels would leave its smaller
	/// levels showing the old image) or more than one layer (a cube's six faces), or texels that are not exactly its
	/// one level, rows tightly packed.
	/// </summary>
	/// <param name="textureId">The texture's id, for the message.</param>
	/// <param name="width">Its width in texels.</param>
	/// <param name="height">Its height in texels.</param>
	/// <param name="bytesPerTexel">The size of one texel of its format.</param>
	/// <param name="mipLevels">How many mip levels it was created with.</param>
	/// <param name="layers">How many layers it has: 6 for a cube, otherwise 1.</param>
	/// <param name="length">How many bytes the update carries.</param>
	public static void Check(int textureId, int width, int height, int bytesPerTexel, int mipLevels, int layers, int length)
	{
		if (mipLevels != 1 || layers != 1)
		{
			throw new InvalidOperationException(
				$"Texture {textureId} cannot be updated in place: it has {mipLevels} mip level(s) and {layers} layer(s), and " +
				"IEngine.UpdateTexture writes one level of one layer. Create a texture that changes with generateMips: false, " +
				"or create a new texture and release the old one.");
		}
		var expected = (long)width * height * bytesPerTexel;
		if (length != expected)
		{
			throw new ArgumentException(
				$"Texture {textureId} is {width}×{height} at {bytesPerTexel} bytes a texel, so an update is {expected} bytes, " +
				$"rows tightly packed; got {length}. A texture's size and format are fixed when it is created.",
				"texels");
		}
	}
}
