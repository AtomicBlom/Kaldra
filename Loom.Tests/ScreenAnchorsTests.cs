using System.Numerics;

using Loom.Core.Geometry;
using Loom.Core.Hosting;
using Loom.UI.Vysara;

using Vysara;
using Vysara.Controls;
using Vysara.Media;

using Ellipse = Vysara.Shapes.Ellipse;

namespace Loom.Tests;

/// <summary>
/// <see cref="ScreenAnchors"/> places each element where its world point projects, under both clip-space conventions;
/// fades it out off-screen and behind the eye on its container, leaving the element's own opacity alone; and moves a
/// hundred anchors a frame without allocating. Headless: a Vysara host and the projection maths, no GPU.
/// </summary>
public class ScreenAnchorsTests
{
	private static readonly Vector2 Framebuffer = new(1280f, 720f);
	private const double Delta = 1.0 / 60.0;

	private static readonly (string Name, Matrix4x4 Correction)[] Conventions =
	[
		("negated Y (Vulkan, DirectX 12)", Matrix4x4.CreateScale(1f, -1f, 1f)),
		("identity (WebGPU)", Matrix4x4.Identity),
	];

	private static readonly Matrix4x4 View = Matrix4x4.CreateLookAt(new Vector3(0f, 0f, 5f), Vector3.Zero, Vector3.UnitY);
	private static readonly Matrix4x4 Projection =
		Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, Framebuffer.X / Framebuffer.Y, 0.1f, 100f);

	private sealed class ClipSpace(Matrix4x4 correction) : IEngineClipSpace
	{
		public Matrix4x4 ClipSpaceCorrection => correction;
	}

	// A host with the anchors on a view of their own, at the given layout scale.
	private static (UIHost Host, ScreenAnchors Anchors) Anchored(Matrix4x4 correction, float layoutScale = 1f)
	{
		var host = new UIHost();
		var view = host.CreateRoot(new UILayer(1, layoutScale));
		return (host, ScreenAnchors.Create(new ClipSpace(correction), view));
	}

	// One frame in the order a host runs it: lay out, place the anchors, composite.
	private static void Frame(UIHost host, ScreenAnchors anchors, in Matrix4x4 viewProjection, int frames = 1)
	{
		for (var i = 0; i < frames; i++)
		{
			host.Tick(Delta, 0f, Framebuffer);
			anchors.Update(viewProjection, Framebuffer, Delta);
			host.Render();
		}
	}

	private static Vector2 TopLeftOf(UIElement element)
	{
		var p = element.TransformToVisual(null).TransformPoint(new Point(0f, 0f));
		return new Vector2(p.X, p.Y);
	}

	private static Border Box() => new() { Width = 40f, Height = 20f, Background = new SolidColorBrush(HdrColor.FromRgb(255, 255, 255)) };

	[Test]
	public void An_anchored_element_sits_on_its_projected_point_under_both_clip_space_conventions()
	{
		foreach (var (name, correction) in Conventions)
		{
			var (host, anchors) = Anchored(correction);
			var viewProjection = View * Projection * correction;
			var world = new Vector3(1f, 1f, 0f);
			viewProjection.TryProjectToScreen(world, Framebuffer, correction, out var point, out _).ShouldBeTrue(name);

			// The middle of its bottom edge on the point, lifted 6 px clear of it.
			var box = Box();
			anchors.Add(box, world, new Vector2(0.5f, 1f), new Vector2(0f, -6f));
			Frame(host, anchors, viewProjection, frames: 3);

			var topLeft = TopLeftOf(box);
			topLeft.X.ShouldBe(point.X - 20f, 1e-2f, name);
			topLeft.Y.ShouldBe(point.Y - 20f - 6f, 1e-2f, name);
			// Up and to the right of the screen's centre on every backend.
			point.X.ShouldBeGreaterThan(Framebuffer.X / 2f, name);
			point.Y.ShouldBeLessThan(Framebuffer.Y / 2f, name);
		}
	}

	[Test]
	public void Anchors_place_in_layout_units_on_a_scaled_view()
	{
		var correction = Conventions[0].Correction;
		var (host, anchors) = Anchored(correction, layoutScale: 2f);
		var viewProjection = View * Projection * correction;
		var box = Box();
		anchors.Add(box, Vector3.Zero, new Vector2(0.5f, 0.5f));
		Frame(host, anchors, viewProjection, frames: 3);

		// The screen's centre is (640, 360) pixels, which is (320, 180) at two pixels per layout unit.
		var topLeft = TopLeftOf(box);
		topLeft.X.ShouldBe(320f - 20f, 1e-2f);
		topLeft.Y.ShouldBe(180f - 10f, 1e-2f);
	}

	[Test]
	public void An_anchor_fades_in_and_follows_its_point()
	{
		var correction = Conventions[0].Correction;
		var (host, anchors) = Anchored(correction);
		var viewProjection = View * Projection * correction;
		var box = Box();
		var id = anchors.Add(box, Vector3.Zero, Vector2.Zero);
		anchors.FadeSeconds = 0.1f;

		// Hidden until the host has laid it out once with its point on screen, then up over FadeSeconds.
		Frame(host, anchors, viewProjection);
		anchors.GetFade(id).ShouldBe(0f);
		Frame(host, anchors, viewProjection);
		anchors.GetFade(id).ShouldBe((float)(Delta / 0.1), 1e-4f);
		Frame(host, anchors, viewProjection, frames: 6);
		anchors.GetFade(id).ShouldBe(1f);

		anchors.SetWorldPosition(id, new Vector3(-1f, -1f, 0f));
		Frame(host, anchors, viewProjection);
		viewProjection.TryProjectToScreen(new Vector3(-1f, -1f, 0f), Framebuffer, correction, out var moved, out _);
		TopLeftOf(box).ShouldBe(moved);
	}

	[Test]
	public void An_anchor_fades_out_behind_the_eye_without_touching_its_elements_opacity()
	{
		var correction = Conventions[1].Correction;
		var (host, anchors) = Anchored(correction);
		var viewProjection = View * Projection * correction;
		var box = Box();
		box.Opacity = 0.7f; // the element's own opacity, which an app may animate
		var id = anchors.Add(box, Vector3.Zero, Vector2.Zero);
		Frame(host, anchors, viewProjection, frames: 30);
		anchors.GetFade(id).ShouldBe(1f);
		var lastSeen = TopLeftOf(box);

		// Behind the eye: it fades out where it was last seen, and leaves composition once it is gone.
		anchors.SetWorldPosition(id, new Vector3(0f, 0f, 10f));
		Frame(host, anchors, viewProjection, frames: 6);
		anchors.GetFade(id).ShouldBeInRange(0.01f, 0.99f);
		TopLeftOf(box).ShouldBe(lastSeen);
		Frame(host, anchors, viewProjection, frames: 12);
		anchors.GetFade(id).ShouldBe(0f);
		box.Opacity.ShouldBe(0.7f);

		// Back in front: it returns and fades in again.
		anchors.SetWorldPosition(id, Vector3.Zero);
		Frame(host, anchors, viewProjection, frames: 30);
		anchors.GetFade(id).ShouldBe(1f);
		box.Opacity.ShouldBe(0.7f);
	}

	[Test]
	public void An_anchor_fades_out_when_its_point_leaves_the_viewport()
	{
		var correction = Conventions[0].Correction;
		var (host, anchors) = Anchored(correction);
		var viewProjection = View * Projection * correction;
		var id = anchors.Add(Box(), Vector3.Zero, Vector2.Zero);
		Frame(host, anchors, viewProjection, frames: 30);
		anchors.GetFade(id).ShouldBe(1f);

		// Far to the right: in front of the eye, but off the viewport.
		anchors.SetWorldPosition(id, new Vector3(20f, 0f, 0f));
		Frame(host, anchors, viewProjection, frames: 30);
		anchors.GetFade(id).ShouldBe(0f);

		// A margin wider than its distance off-screen keeps it shown.
		anchors.ViewportMargin = 10000f;
		Frame(host, anchors, viewProjection, frames: 30);
		anchors.GetFade(id).ShouldBe(1f);
	}

	[Test]
	public void A_removed_anchor_lets_its_element_go_and_its_id_dies()
	{
		var (host, anchors) = Anchored(Conventions[0].Correction);
		var box = Box();
		var id = anchors.Add(box, Vector3.Zero, Vector2.Zero);
		anchors.Remove(id);
		anchors.Count.ShouldBe(0);

		Should.Throw<ArgumentException>(() => anchors.SetWorldPosition(id, Vector3.One));
		Should.Throw<ArgumentException>(() => anchors.Remove(id));
		// The slot is reused by the next anchor, but the old id still names nothing.
		var next = anchors.Add(box, Vector3.Zero, Vector2.Zero);
		next.Index.ShouldBe(id.Index);
		Should.Throw<ArgumentException>(() => anchors.GetFade(id));
		anchors.GetFade(next).ShouldBe(0f);
	}

	[Test]
	public void Anchors_need_a_view_of_their_own()
	{
		var host = new UIHost();
		var view = host.CreateRoot(UILayer.Default);
		view.Root.Content = new Border();
		Should.Throw<ArgumentException>(() => ScreenAnchors.Create(new ClipSpace(Matrix4x4.Identity), view));
	}

	/// <summary>
	/// A hundred anchors, every one moving every frame under an orbiting view, cost no allocation once warm: placing
	/// them, laying the host out and compositing it. Some cross the viewport's edge as the view turns, so containers fade
	/// and collapse and come back within the measured frames too. The elements are a mix of what labels are made of:
	/// shapes, rounded boxes and text.
	/// </summary>
	[Test]
	[NotInParallel]
	public void Moving_a_hundred_anchors_allocates_nothing_once_warm()
	{
		var correction = Conventions[0].Correction;
		var (host, anchors) = Anchored(correction);
		anchors.FadeSeconds = 0.05f;
		var ids = new ScreenAnchorId[100];
		var fill = new SolidColorBrush(HdrColor.FromRgb(80, 170, 255));
		for (var i = 0; i < ids.Length; i++)
		{
			UIElement element = (i % 3) switch
			{
				0 => new Ellipse { Width = 12f, Height = 12f, Fill = fill },
				1 => new Border { Width = 30f, Height = 14f, CornerRadius = new CornerRadius(4f), Background = fill },
				_ => new TextBlock { Text = $"label {i}", FontSize = 12f, Foreground = fill },
			};
			ids[i] = anchors.Add(element, Ring(i, 0f), new Vector2(0.5f, 1f), new Vector2(0f, -4f));
		}

		// How many anchor-frames were shown, part-faded and hidden: the guard that the measured frames move containers
		// through every state rather than holding them all shown.
		int shown = 0, fading = 0, hidden = 0;
		void Run(int from, int to)
		{
			for (var f = from; f < to; f++)
			{
				var t = f / 60f;
				var eye = new Vector3(MathF.Sin(t) * 6f, 2f, MathF.Cos(t) * 6f);
				var viewProjection = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY) * Projection * correction;
				for (var i = 0; i < ids.Length; i++)
					anchors.SetWorldPosition(ids[i], Ring(i, t));
				host.Tick(Delta, t, Framebuffer);
				anchors.Update(viewProjection, Framebuffer, Delta);
				host.Render();
				for (var i = 0; i < ids.Length; i++)
				{
					var fade = anchors.GetFade(ids[i]);
					if (fade >= 1f) shown++;
					else if (fade <= 0f) hidden++;
					else fading++;
				}
			}
		}

		Run(0, 240); // warm: every container laid out, faded, collapsed and back at least once
		(shown, fading, hidden) = (0, 0, 0);
		// Each window carries on from the last, so the camera keeps moving rather than jumping back.
		var next = 240;
		Allocations.ShouldAllocateNothing(() =>
		{
			Run(next, next + 240);
			next += 240;
		}, "placing, laying out and compositing moving anchors allocated on warm frames.");
		shown.ShouldBeGreaterThan(0);
		fading.ShouldBeGreaterThan(0);
		hidden.ShouldBeGreaterThan(0);
	}

	// A ring of points, wider than the view, spinning slowly, so some leave and re-enter the viewport.
	private static Vector3 Ring(int i, float t)
	{
		var a = i * (MathF.Tau / 100f) + t * 0.5f;
		return new Vector3(MathF.Cos(a) * 4f, MathF.Sin(i * 0.7f) * 0.5f, MathF.Sin(a) * 4f);
	}
}
