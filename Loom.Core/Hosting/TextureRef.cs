using Loom.Core.Graph;
using Loom.Core.Materials;

namespace Loom.Core.Hosting;

/// <summary>
/// What one slot of an <see cref="IRenderContext.DrawList{TVertex}"/> samples: either an engine <see cref="Texture"/>
/// uploaded from CPU pixels, or a graph texture (an <see cref="ITextureHandle"/>) that an earlier pass rendered this
/// frame — a minimap, a portal view, a blurred backdrop shown inside a UI.
/// <para>
/// A graph texture is read like any other sampled input: the pass submitting the list must declare it with
/// <c>.Samples</c>, which is what orders it after its producer and puts it in a sampleable layout. Submitting an
/// undeclared one throws <see cref="PassBindingException"/>. The backend resolves it to the resource's live image when
/// the list records, so the reference stays valid across resizes and graph swaps that keep the handle.
/// </para>
/// <para>
/// A value type holding at most one reference, so a producer keeps an array of these and rewrites it in place without
/// allocating. <c>default</c> names nothing and is refused where a texture is required.
/// </para>
/// </summary>
public readonly struct TextureRef : IEquatable<TextureRef>
{
	private readonly Texture _texture;

	/// <summary>Sample an engine texture.</summary>
	public TextureRef(Texture texture) => _texture = texture;

	/// <summary>
	/// Sample a graph texture. <paramref name="handle"/> must come from the graph the engine is running; the pass that
	/// submits the list declares it with <c>.Samples</c>.
	/// </summary>
	/// <exception cref="ArgumentException">The handle is not a graph resource (a foreign <see cref="ITextureHandle"/> implementation).</exception>
	public TextureRef(ITextureHandle handle)
	{
		ArgumentNullException.ThrowIfNull(handle);
		Resource = handle as ResourceHandle
			?? throw new ArgumentException($"'{handle.GetType().Name}' is not a graph resource; a TextureRef samples a handle the graph declared.", nameof(handle));
	}

	/// <summary>The graph resource this samples, or null when it samples an engine <see cref="Texture"/>.</summary>
	public ResourceHandle? Resource { get; }

	/// <summary>Whether this samples a graph texture rather than an engine one.</summary>
	public bool IsGraph => Resource is not null;

	/// <summary>Whether this names nothing: neither a graph resource nor a minted engine texture.</summary>
	public bool IsEmpty => Resource is null && _texture.Id == 0;

	/// <summary>The engine texture this samples.</summary>
	/// <exception cref="InvalidOperationException">This samples a graph texture.</exception>
	public Texture Texture => Resource is null
		? _texture
		: throw new InvalidOperationException($"This TextureRef samples graph texture '{Resource.Name}', not an engine Texture.");

	/// <summary>Sample an engine texture.</summary>
	public static implicit operator TextureRef(Texture texture) => new(texture);

	/// <summary>Sample a graph colour target.</summary>
	public static implicit operator TextureRef(ColorHandle handle) => new(handle);

	/// <summary>Sample a graph depth target.</summary>
	public static implicit operator TextureRef(DepthHandle handle) => new(handle);

	/// <inheritdoc/>
	public bool Equals(TextureRef other) => ReferenceEquals(Resource, other.Resource) && _texture == other._texture;

	/// <inheritdoc/>
	public override bool Equals(object? obj) => obj is TextureRef other && Equals(other);

	/// <inheritdoc/>
	public override int GetHashCode() => Resource is null ? _texture.GetHashCode() : Resource.Id;

	/// <summary>Whether both name the same texture.</summary>
	public static bool operator ==(TextureRef left, TextureRef right) => left.Equals(right);

	/// <summary>Whether they name different textures.</summary>
	public static bool operator !=(TextureRef left, TextureRef right) => !left.Equals(right);

	/// <inheritdoc/>
	public override string ToString() => Resource is null ? _texture.ToString() : $"Graph({Resource.Name})";
}
