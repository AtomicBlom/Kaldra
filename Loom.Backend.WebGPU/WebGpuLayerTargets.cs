using System.Runtime.Versioning;

using Loom.Graph;

namespace Loom.Backend.WebGPU;

/// <summary>
/// The renderer-private offscreen targets backing UI group-opacity layers (the <c>BeginLayer</c>/<c>EndLayer</c>
/// markers a <c>ctx.DrawList</c> submission carries). These are <em>not</em> graph resources — they live below the
/// graph seam so the render graph still sees exactly one <c>ui</c> pass and its hash never moves when an opacity
/// crosses 1.0. A layer target matches the scope its list draws into, so textures are pooled per (size, colour format)
/// — one frame can layer UI on targets of several sizes, a HUD on the canvas and a panel on its own texture — and
/// within a size per nesting depth: nested layers need distinct textures at once, while sibling layers at the same
/// depth reuse one (WebGPU's automatic same-queue ordering sequences the prior composite's read before the next
/// clear). Pooling at the pass colour format lets the already-baked <c>ui</c> pipeline render into a layer unchanged.
/// <para>
/// Single-buffered like the rest of the backend: WebGPU orders successive queue submits with automatic cross-submit
/// synchronisation, so a layer texture is never read by a still-in-flight earlier frame while this frame clears it.
/// Textures are kept across frames, so steady-state layering allocates nothing, and are released only in
/// <see cref="BeginFrame"/>, before the frame records: every command naming them has been submitted by then, which
/// WebGPU lets complete, while a texture released mid-frame would fail the frame's submit. A size the UI stops
/// layering at (one a resize left behind) is retired after <see cref="RetireAfterIdleFrames"/> frames without it.
/// </para>
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class WebGpuLayerTargets : IDisposable
{
	/// <summary>
	/// How many frames a size's textures are kept without layering at it: long enough that a UI layering on and off (a
	/// fade now and then) keeps them, short enough that sizes a resize leaves behind go within a second.
	/// </summary>
	internal const int RetireAfterIdleFrames = 60;

	internal readonly record struct Target(int Texture, int View);

	// One size's textures, depth-indexed.
	private sealed class SizeClass(int width, int height, int formatId)
	{
		public readonly int Width = width, Height = height, FormatId = formatId;
		public readonly List<Target> Pool = [];
		public bool UsedThisFrame;
		public int IdleFrames;

		public bool Matches(int width, int height, int formatId) => Width == width && Height == height && FormatId == formatId;
	}

	private readonly WebGpuHandleAllocator _handles;
	private readonly List<SizeClass> _classes = [];
	private int _width;
	private int _height;
	private int _formatId = -1;
	private SizeClass? _current; // the class matching _width/_height/_formatId, found on the first Acquire after Configure

	public WebGpuLayerTargets(WebGpuHandleAllocator handles) => _handles = handles;

	/// <summary>
	/// Start a frame: a size that has gone <see cref="RetireAfterIdleFrames"/> frames without layering is released,
	/// its views first evicted from <paramref name="context"/>'s bind-group cache — the allocator recycles a released
	/// handle, so a cached group naming it would bind whatever texture takes the handle next.
	/// </summary>
	public void BeginFrame(WebGpuRenderContext context)
	{
		for (var i = _classes.Count - 1; i >= 0; i--)
		{
			var size = _classes[i];
			if (size.UsedThisFrame)
			{
				size.UsedThisFrame = false;
				size.IdleFrames = 0;
				continue;
			}
			if (++size.IdleFrames < RetireAfterIdleFrames)
				continue;

			foreach (var t in size.Pool)
				context.EvictBindGroups(BoundObject.View, t.View);
			ReleaseTargets(size.Pool);
			_classes.RemoveAt(i);
			if (ReferenceEquals(_current, size))
				_current = null;
		}
	}

	/// <summary>
	/// Bind the pool to the size and colour format of the scope about to draw a list. Textures of other sizes are kept
	/// for the lists that draw at those sizes. Free, and allocates nothing: a list that opens no layer never makes a
	/// size's pool.
	/// </summary>
	public void Configure(int width, int height, int formatId)
	{
		if (_width == width && _height == height && _formatId == formatId)
			return;
		_width = width;
		_height = height;
		_formatId = formatId;
		_current = null;
	}

	/// <summary>Get (creating on first use) the layer target at the configured size and nesting <paramref name="depth"/>.</summary>
	public Target Acquire(int depth)
	{
		var size = _current ??= FindOrAddClass();
		size.UsedThisFrame = true;
		var pool = size.Pool;
		while (pool.Count <= depth)
			pool.Add(Create(size, pool.Count));
		return pool[depth];
	}

	private SizeClass FindOrAddClass()
	{
		for (var i = 0; i < _classes.Count; i++)
		{
			if (_classes[i].Matches(_width, _height, _formatId))
				return _classes[i];
		}
		var added = new SizeClass(_width, _height, _formatId);
		_classes.Add(added);
		return added;
	}

	private Target Create(SizeClass size, int depth)
	{
		var texture = _handles.Allocate(WebGpuHandleAllocator.Textures);
		var label = $"ui.layer#{depth}@{size.Width}x{size.Height}";
		WebGpuInterop.CreateTexture(texture, size.Width, size.Height, 1, size.FormatId,
			WebGpuStateMapping.RenderAttachment | WebGpuStateMapping.TextureBinding, 1, label);
		var view = _handles.Allocate(WebGpuHandleAllocator.Views);
		WebGpuInterop.CreateTextureView(view, texture, size.FormatId, 0, 1, label + ".view");
		return new Target(texture, view);
	}

	private void ReleaseTargets(List<Target> pool)
	{
		foreach (var t in pool)
		{
			_handles.Release(WebGpuHandleAllocator.Views, t.View);
			_handles.Release(WebGpuHandleAllocator.Textures, t.Texture);
		}
		pool.Clear();
	}

	public void Dispose()
	{
		foreach (var size in _classes)
			ReleaseTargets(size.Pool);
		_classes.Clear();
		_current = null;
	}
}
