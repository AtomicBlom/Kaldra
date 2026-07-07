using System.Runtime.Versioning;

namespace Loom.Backend.WebGPU;

/// <summary>
/// The renderer-private offscreen targets backing UI group-opacity layers (the <c>BeginLayer</c>/<c>EndLayer</c>
/// markers a <c>ctx.DrawList</c> submission carries). These are <em>not</em> graph resources — they live below the
/// graph seam so the render graph still sees exactly one <c>ui</c> pass and its hash never moves when an opacity
/// crosses 1.0. One full-surface colour texture is pooled per nesting depth: nested layers need distinct textures
/// at once, while sibling layers at the same depth reuse one (WebGPU's automatic same-queue ordering sequences the
/// prior composite's read before the next clear). Textures are kept across frames (retired, not freed) so
/// steady-state layering allocates nothing; they are recreated when the surface size or colour format changes.
/// Pooling at the pass colour format lets the already-baked <c>ui</c> pipeline render into a layer unchanged.
/// <para>
/// Single-buffered like the rest of the backend: WebGPU orders successive queue submits with automatic cross-submit
/// synchronisation, so a layer texture is never read by a still-in-flight earlier frame while this frame clears it.
/// </para>
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class WebGpuLayerTargets : IDisposable
{
	internal readonly record struct Target(int Texture, int View);

	private readonly WebGpuHandleAllocator _handles;
	private readonly List<Target> _pool = [];
	private int _width;
	private int _height;
	private int _formatId = -1;

	public WebGpuLayerTargets(WebGpuHandleAllocator handles) => _handles = handles;

	/// <summary>
	/// Bind the pool to the current surface size + colour format. A change (window resize, HDR toggle) discards every
	/// pooled texture — they would no longer match the resumed pass attachment. A no-op when unchanged, so the common
	/// per-frame call is free.
	/// </summary>
	public void Configure(int width, int height, int formatId)
	{
		if (_width == width && _height == height && _formatId == formatId)
			return;
		ReleaseTargets();
		_width = width;
		_height = height;
		_formatId = formatId;
	}

	/// <summary>Get (creating on first use) the layer target at nesting <paramref name="depth"/>.</summary>
	public Target Acquire(int depth)
	{
		while (_pool.Count <= depth)
			_pool.Add(Create(_pool.Count));
		return _pool[depth];
	}

	private Target Create(int depth)
	{
		var texture = _handles.Allocate(WebGpuHandleAllocator.Textures);
		WebGpuInterop.CreateTexture(texture, _width, _height, 1, _formatId,
			WebGpuStateMapping.RenderAttachment | WebGpuStateMapping.TextureBinding, 1, $"ui.layer#{depth}");
		var view = _handles.Allocate(WebGpuHandleAllocator.Views);
		WebGpuInterop.CreateTextureView(view, texture, _formatId, 0, 1, $"ui.layer#{depth}.view");
		return new Target(texture, view);
	}

	private void ReleaseTargets()
	{
		foreach (var t in _pool)
		{
			_handles.Release(WebGpuHandleAllocator.Views, t.View);
			_handles.Release(WebGpuHandleAllocator.Textures, t.Texture);
		}
		_pool.Clear();
	}

	public void Dispose() => ReleaseTargets();
}
