using System.Numerics;

namespace BinaryVibrance.Kaldra.Core.Graph;

/// <summary>
/// A colour image target. Produced by <c>.Writes</c>/<c>.Blends</c>, read by <c>.Samples</c>, and (as a
/// storage image — e.g. the persistent exposure value) by <c>.ReadWrites</c>. Also the static type of the
/// <see cref="GraphBuilder.Backbuffer"/> and <see cref="GraphBuilder.Persistent"/> image handles, which are
/// colour-like (their distinct lifetime is carried by <see cref="ResourceHandle.Kind"/>).
/// </summary>
public sealed class ColorHandle : ResourceHandle, ITextureHandle, IStorageHandle
{
	/// <summary>
	/// The colour to clear this target to when a pass first writes it. <c>null</c> means clear to
	/// <see cref="Vector4.Zero"/> (transparent black — the safe default for g-buffer targets and UI textures).
	/// Only meaningful when <see cref="NoClear"/> is <c>false</c>.
	/// </summary>
	public Vector4? ClearValue { get; }

	/// <summary>
	/// When <c>true</c> the engine emits <c>LoadOp.DontCare</c> on the first write instead of clearing,
	/// saving the GPU bandwidth of a clear. Only correct when the first pass that writes this target is
	/// guaranteed to cover every pixel (fullscreen passes, bloom pyramid, SSAO output, etc.).
	/// </summary>
	public bool NoClear { get; }

	internal ColorHandle(int id, string name, Format format, ResourceKind kind, Domain domain, float scale, int count, Vector4? clearValue = null, bool noClear = false)
		: base(id, name, format, kind, domain, scale, count, byteSize: 0)
	{
		ClearValue = clearValue;
		NoClear = noClear;
	}
}
