namespace Loom.Core.Graph;

/// <summary>A depth/stencil target. Written by <c>.WritesDepth</c>, tested by <c>.ReadsDepth</c>, and sampleable.</summary>
public sealed class DepthHandle : ResourceHandle, ITextureHandle
{
	/// <summary>
	/// The value the engine clears this target to before the first pass writes it. 1 (the far plane) for a
	/// conventional depth buffer; 0 for reversed-Z, which must be paired with a reversed projection and a
	/// <see cref="DepthCompare.GreaterOrEqual"/> test on the techniques that draw into it.
	/// </summary>
	public float ClearDepth { get; }

	/// <summary>
	/// When true the engine skips the clear on first write (<c>LoadOp.DontCare</c>), leaving
	/// <see cref="ClearDepth"/> unused. Only correct when the first pass to write depth covers every pixel.
	/// </summary>
	public bool NoClear { get; }

	internal DepthHandle(int id, string name, Format format, Domain domain, float scale, float clearDepth = 1f, bool noClear = false)
		: base(id, name, format, ResourceKind.Depth, domain, scale, count: 1, byteSize: 0)
	{
		ClearDepth = clearDepth;
		NoClear = noClear;
	}
}
