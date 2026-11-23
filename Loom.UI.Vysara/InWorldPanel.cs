using System.Numerics;
using Loom.Core.Geometry;
using Loom.Core.Hosting;
using BinaryVibrance.Vysara;
using BinaryVibrance.Vysara.Input;
// BinaryVibrance.Vysara.Drawing and Loom.Core.Hosting both export DrawListId — alias to pin the Vysara side (matches UIPainter.cs).
using DrawListId = BinaryVibrance.Vysara.Drawing.DrawListId;

namespace Loom.UI.Vysara;

/// <summary>
/// An interactive UI panel placed in the 3D world — renders a Vysara surface into an offscreen texture via the
/// standard "ui" draw-list path, and maps screen-space pointer events onto the panel's 2D surface by intersecting
/// a camera ray with the panel's world-space quad. From Vysara's perspective it is just a 2D surface with its own
/// <see cref="UIHost"/>; all 3D concerns (world transform, billboard, ray unprojection) live here in the bridge.
/// </summary>
/// <remarks>
/// Usage pattern per frame:
/// <list type="number">
/// <item>Queue screen-space pointer events with <see cref="QueueScreenPointer"/> (alongside the main uiHost).</item>
/// <item>Call <see cref="Tick"/> with the current camera matrices — projects hits onto the panel and drives layout.</item>
/// <item>Call <see cref="Render"/> to composite the visual tree and prepare it for drawing, before the engine renders.</item>
/// <item>Submit the composited draw list from a render-graph pass via <see cref="Draw"/>.</item>
/// </list>
/// </remarks>
public sealed class InWorldPanel
{
	private readonly UIHost _host;
	private readonly UIView _view;
	private readonly UIPainter _painter;
	private readonly Queue<PointerInput> _pendingPointers = new();
	private readonly int _pixelWidth;
	private readonly int _pixelHeight;
	private readonly bool _billboard;
	// The engine's ClipSpaceCorrection, read once at creation: the same correction the camera composed into the
	// view-projection Tick receives, so turning a screen position into a ray can never drift out of sync with the
	// backend's NDC Y direction the way a hardcoded assumption would.
	private readonly Matrix4x4 _clipSpaceCorrection;

	private InWorldPanel(UIHost host, UIView view, UIPainter painter,
			int pixelWidth, int pixelHeight, Matrix4x4 worldTransform, bool billboard, Matrix4x4 clipSpaceCorrection)
	{
		_host = host;
		_view = view;
		_painter = painter;
		_pixelWidth = pixelWidth;
		_pixelHeight = pixelHeight;
		WorldTransform = worldTransform;
		_billboard = billboard;
		_clipSpaceCorrection = clipSpaceCorrection;
	}

	/// <summary>
	/// Create an in-world panel. The panel texture's resolution is <paramref name="pixelWidth"/>×<paramref name="pixelHeight"/>.
	/// The physical world size of the quad is expressed by the <b>scale baked into <paramref name="worldTransform"/></b> — use a
	/// unit quad mesh (vertices at ±0.5 in XY) and let the transform's scale set the world dimensions.
	/// </summary>
	public static InWorldPanel Create(
		IEngine engine,
		int pixelWidth,
		int pixelHeight,
		Matrix4x4 worldTransform,
		bool billboard = false,
		float layoutScale = 1f)
	{
		var host = new UIHost();
		var view = host.CreateRoot(new UILayer(0, layoutScale), interactive: true);
		var painter = UIPainter.Create(engine, host);
		return new InWorldPanel(host, view, painter, pixelWidth, pixelHeight, worldTransform, billboard, engine.ClipSpaceCorrection);
	}

	/// <summary>The underlying <see cref="UIView"/>. Set <c>View.Root.Content</c> to assign the panel's UI tree.</summary>
	public UIView View => _view;

	/// <summary>The draw-list id this panel composites into. Pass to <see cref="Draw"/> from a render-graph pass.</summary>
	public DrawListId Surface => _view.Surface;

	/// <summary>The panel texture width in pixels.</summary>
	public int PixelWidth => _pixelWidth;

	/// <summary>The panel texture height in pixels.</summary>
	public int PixelHeight => _pixelHeight;

	/// <summary>
	/// The panel's world transform. Scale determines the physical quad size; rotation sets orientation.
	/// Updated each frame by billboard mode when <c>billboard: true</c> was passed to <see cref="Create"/>.
	/// </summary>
	public Matrix4x4 WorldTransform { get; set; }

	/// <summary>
	/// The last successful screen-to-panel projection for this frame, in panel pixels [0..PixelWidth) × [0..PixelHeight).
	/// Null when no queued pointer event hit the quad. Reset at the start of each <see cref="Tick"/>.
	/// </summary>
	public Vector2? LastHitPixel { get; private set; }

	/// <summary>
	/// Queue a screen-space pointer event for this panel. Call alongside the main <c>uiHost.QueuePointer</c> for
	/// every mouse/touch event. Events are stored; the hit-test happens in <see cref="Tick"/> once the camera
	/// matrices are available.
	/// </summary>
	public void QueueScreenPointer(in PointerInput e) => _pendingPointers.Enqueue(e);

	/// <summary>
	/// Drive the panel for one frame: unprojects queued screen-pointer events onto the panel surface (discarding
	/// misses), optionally updates <see cref="WorldTransform"/> for billboard, then drives internal layout and routing.
	/// </summary>
	/// <param name="deltaSeconds">Frame delta in seconds.</param>
	/// <param name="time">Elapsed time in seconds (drives animations).</param>
	/// <param name="view">Camera view matrix — used for billboard rotation and ray construction.</param>
	/// <param name="viewProjection">Camera view-projection (clip-space-corrected) — used for ray unprojection.</param>
	/// <param name="framebufferSize">Window framebuffer size in pixels — used to convert screen coords to NDC.</param>
	public void Tick(double deltaSeconds, float time, Matrix4x4 view, Matrix4x4 viewProjection, Vector2 framebufferSize)
	{
		if (_billboard)
			UpdateBillboard(view);

		// Process screen-space events: project each onto the panel; queue remapped events for the internal host.
		LastHitPixel = null;
		while (_pendingPointers.TryDequeue(out var e))
		{
			if (TryUnprojectToPanel(e.Position, viewProjection, framebufferSize, out var uiLocal))
			{
				_host.QueuePointer(e with { Position = uiLocal });
				LastHitPixel = uiLocal;
			}
		}

		_host.Tick(deltaSeconds, time, new Vector2(_pixelWidth, _pixelHeight));
	}

	/// <summary>
	/// Composite the panel's visual tree to a draw list and prepare it for <see cref="Draw"/> (uploading any image it
	/// newly references — see <see cref="UIPainter.Prepare"/>). Call once per frame after <see cref="Tick"/> and before
	/// <see cref="IEngine.Render"/>, never from a pass.
	/// </summary>
	public void Render()
	{
		_host.Render();
		_painter.Prepare();
	}

	/// <summary>
	/// Submit the composited draw list through a render-graph pass. Call from a pass that <c>.Writes</c> the panel
	/// texture — this is the UI render into the offscreen target, before the world-space quad samples it.
	/// </summary>
	public void Draw(IRenderContext ctx) => _painter.Draw(ctx, Surface);

	/// <summary>
	/// Where a point on the panel appears on screen — the inverse of the projection <see cref="Tick"/> applies to queued
	/// screen pointers: panel pixels → the quad in the world (through <see cref="WorldTransform"/>, which a billboard
	/// updates in <see cref="Tick"/>) → <paramref name="viewProjection"/> → framebuffer pixels. Null when the point is
	/// behind the camera.
	/// </summary>
	/// <param name="panelPixel">A point on the panel, in panel pixels [0..PixelWidth) × [0..PixelHeight).</param>
	/// <param name="viewProjection">Camera view-projection (clip-space-corrected), as passed to <see cref="Tick"/>.</param>
	/// <param name="framebufferSize">Window framebuffer size in pixels.</param>
	public Vector2? ProjectToScreen(Vector2 panelPixel, Matrix4x4 viewProjection, Vector2 framebufferSize)
	{
		// Panel pixels → quad-local (a unit XY plane, +Y up) → world → framebuffer pixels, undoing TryUnprojectToPanel's
		// mapping through the same clip-space correction.
		var local = new Vector3((panelPixel.X / _pixelWidth) - 0.5f, 0.5f - (panelPixel.Y / _pixelHeight), 0f);
		return viewProjection.TryProjectToScreen(Vector3.Transform(local, WorldTransform), framebufferSize, _clipSpaceCorrection, out var screen, out _)
			? screen
			: null;
	}

	private void UpdateBillboard(Matrix4x4 view)
	{
		if (!Matrix4x4.Invert(view, out var inverseView))
			return;

		var cameraPos = inverseView.Translation;
		var pos = WorldTransform.Translation;

		// Preserve the transform's scale by measuring the row lengths.
		var scaleX = new Vector3(WorldTransform.M11, WorldTransform.M12, WorldTransform.M13).Length();
		var scaleY = new Vector3(WorldTransform.M21, WorldTransform.M22, WorldTransform.M23).Length();
		var scaleZ = new Vector3(WorldTransform.M31, WorldTransform.M32, WorldTransform.M33).Length();

		// Build a face-camera rotation: +Z faces the camera, +Y stays near world up.
		var forward = Vector3.Normalize(cameraPos - pos);
		var right = Vector3.Cross(Vector3.UnitY, forward);
		if (right.LengthSquared() < 1e-6f)
			right = Vector3.UnitX; // camera directly above/below — fall back to +X
		else
			right = Vector3.Normalize(right);
		var up = Vector3.Cross(forward, right);

		WorldTransform = new Matrix4x4(
			right.X * scaleX, right.Y * scaleX, right.Z * scaleX, 0,
			up.X * scaleY, up.Y * scaleY, up.Z * scaleY, 0,
			forward.X * scaleZ, forward.Y * scaleZ, forward.Z * scaleZ, 0,
			pos.X, pos.Y, pos.Z, 1);
	}

	private bool TryUnprojectToPanel(Vector2 screenPos, Matrix4x4 viewProjection, Vector2 framebufferSize, out Vector2 uiLocal)
	{
		uiLocal = default;

		// The ray under the pointer, from the near plane (NDC Z=0) to the far one (Z=1). viewProjection is
		// clip-space-corrected, and the same correction tells the unprojection which way the backend's NDC Y runs, so
		// screen Y=0 (top) lands on whichever NDC Y the active backend puts at the top.
		if (!viewProjection.TryUnprojectFromScreen(screenPos, 0f, framebufferSize, _clipSpaceCorrection, out var nearW)
			|| !viewProjection.TryUnprojectFromScreen(screenPos, 1f, framebufferSize, _clipSpaceCorrection, out var farW))
			return false;
		var rayDir = Vector3.Normalize(farW - nearW);

		// Transform ray into quad-local space. The quad is a unit XY plane (Z = 0, spanning [-0.5, 0.5]²).
		if (!Matrix4x4.Invert(WorldTransform, out var invWorld))
			return false;

		var originL = Vector3.Transform(nearW, invWorld);
		var dirL = Vector3.TransformNormal(rayDir, invWorld);

		if (MathF.Abs(dirL.Z) < 1e-6f)
			return false; // ray parallel to quad plane

		var t = -originL.Z / dirL.Z;
		if (t <= 0)
			return false; // intersection behind camera

		var hit = originL + dirL * t;
		// hit.XY is in quad-local [-0.5, 0.5]; map to UV [0, 1], then to panel pixels.
		var u = hit.X + 0.5f;
		var v = 0.5f - hit.Y; // +Y local = up → v increases downward to match screen convention
		if (u is < 0 or > 1 || v is < 0 or > 1)
			return false; // outside the quad

		uiLocal = new Vector2(u * _pixelWidth, v * _pixelHeight);
		return true;
	}
}
