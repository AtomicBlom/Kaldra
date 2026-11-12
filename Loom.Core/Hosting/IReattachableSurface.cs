namespace Loom.Core.Hosting;

/// <summary>
/// An engine whose window surface can be taken away and given back without losing anything else: its meshes,
/// textures, pipelines, graph and the app's state all stay. For a platform that takes the window's surface away while
/// the app is out of sight, as Android does whenever the app leaves the screen, so that coming back does not mean
/// reloading everything. A host checks for it with <c>engine is IReattachableSurface</c>; a backend that cannot do it
/// does not implement it, and its host keeps the old behaviour of shutting the engine down.
/// </summary>
public interface IReattachableSurface
{
	/// <summary>True while the engine has a surface to present to; false between <see cref="DetachSurface"/> and <see cref="AttachSurface"/>.</summary>
	bool HasSurface { get; }

	/// <summary>
	/// Let go of the surface, after the GPU has finished with it: the platform may destroy it once this returns. The
	/// host must not render until <see cref="AttachSurface"/> gives the engine a surface again; a render without one
	/// throws. Calling it with no surface attached does nothing.
	/// </summary>
	void DetachSurface();

	/// <summary>
	/// Present to <paramref name="surface"/> from now on, sized <paramref name="width"/>×<paramref name="height"/>.
	/// <paramref name="surface"/> is the host's native surface, in the form the backend took at creation (for Vulkan,
	/// an <c>IVkSurface</c>). Throws when a surface is still attached, or when the engine's device cannot present to the
	/// new one.
	/// </summary>
	void AttachSurface(object surface, int width, int height);
}
