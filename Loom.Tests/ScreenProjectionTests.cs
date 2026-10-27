using System.Numerics;

using Loom.Core.Geometry;

namespace Loom.Tests;

/// <summary>
/// <see cref="Matrix4x4ScreenExtensions"/> puts a world point on the same viewport pixel under both clip-space
/// conventions Loom's backends use: a correction that negates Y (Vulkan and DirectX 12) and none (WebGPU). Each test
/// composes the correction into the view-projection as an app does, and passes the same correction to the helper.
/// </summary>
public class ScreenProjectionTests
{
	private static readonly Vector2 Viewport = new(1280f, 720f);

	// The two conventions: (name, correction).
	private static readonly (string Name, Matrix4x4 Correction)[] Conventions =
	[
		("negated Y (Vulkan, DirectX 12)", Matrix4x4.CreateScale(1f, -1f, 1f)),
		("identity (WebGPU)", Matrix4x4.Identity),
	];

	// An eye 5 units up +Z looking at the origin, +Y up, with a 60° vertical field of view.
	private static readonly Matrix4x4 View = Matrix4x4.CreateLookAt(new Vector3(0f, 0f, 5f), Vector3.Zero, Vector3.UnitY);
	private static readonly Matrix4x4 Projection =
		Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, Viewport.X / Viewport.Y, 0.1f, 100f);

	[Test]
	public void A_point_lands_on_the_same_pixel_under_both_clip_space_conventions()
	{
		// (1, 1, 0) is up and to the right of the view axis, 5 units in front of the eye. A pinhole camera puts it at
		// x = 1 / (5 · tan 30° · aspect) and y = 1 / (5 · tan 30°) in NDC, with +Y up.
		var tan = MathF.Tan(MathF.PI / 6f);
		var ndc = new Vector2(1f / (5f * tan * (Viewport.X / Viewport.Y)), 1f / (5f * tan));
		var expected = new Vector2((ndc.X + 1f) * 0.5f * Viewport.X, (1f - ndc.Y) * 0.5f * Viewport.Y);

		foreach (var (name, correction) in Conventions)
		{
			var viewProjection = View * Projection * correction;
			viewProjection.TryProjectToScreen(new Vector3(1f, 1f, 0f), Viewport, correction, out var screen, out var depth)
				.ShouldBeTrue(name);
			screen.X.ShouldBe(expected.X, 1e-2f, name);
			screen.Y.ShouldBe(expected.Y, 1e-2f, name);
			// Up on screen is towards row 0, whichever way the backend's NDC Y runs.
			screen.Y.ShouldBeLessThan(Viewport.Y / 2f, name);
			depth.ShouldBeInRange(0f, 1f, name);

			viewProjection.TryProjectToScreen(Vector3.Zero, Viewport, correction, out var centre, out _).ShouldBeTrue(name);
			centre.X.ShouldBe(Viewport.X / 2f, 1e-3f, name);
			centre.Y.ShouldBe(Viewport.Y / 2f, 1e-3f, name);
		}
	}

	[Test]
	public void A_point_behind_the_eye_does_not_project()
	{
		foreach (var (name, correction) in Conventions)
		{
			var viewProjection = View * Projection * correction;
			// Behind the eye, and on the eye's own plane: a perspective divide there mirrors the point or divides by zero.
			viewProjection.TryProjectToScreen(new Vector3(0.5f, 0.5f, 10f), Viewport, correction, out _, out _).ShouldBeFalse(name);
			viewProjection.TryProjectToScreen(new Vector3(0.5f, 0.5f, 5f), Viewport, correction, out _, out _).ShouldBeFalse(name);
			// In front of the eye but outside the viewport still projects, so a caller can see how far off-screen it is.
			viewProjection.TryProjectToScreen(new Vector3(20f, 0f, 0f), Viewport, correction, out var off, out _).ShouldBeTrue(name);
			off.X.ShouldBeGreaterThan(Viewport.X, name);
		}
	}

	[Test]
	public void Unprojecting_a_projected_point_returns_it()
	{
		var points = new[] { new Vector3(1f, 1f, 0f), new Vector3(-2f, 0.5f, -3f), new Vector3(0.25f, -1.5f, 2f) };
		foreach (var (name, correction) in Conventions)
		{
			var viewProjection = View * Projection * correction;
			foreach (var world in points)
			{
				viewProjection.TryProjectToScreen(world, Viewport, correction, out var screen, out var depth).ShouldBeTrue(name);
				viewProjection.TryUnprojectFromScreen(screen, depth, Viewport, correction, out var back).ShouldBeTrue(name);
				Vector3.Distance(back, world).ShouldBeLessThan(1e-3f, $"{name}: {world} came back as {back}");
			}
		}
	}

	[Test]
	public void An_orthographic_view_projection_projects_too()
	{
		var ortho = Matrix4x4.CreateOrthographic(16f, 9f, 0.1f, 100f);
		foreach (var (name, correction) in Conventions)
		{
			var viewProjection = View * ortho * correction;
			// The top-left corner of the view volume, anywhere along the view axis, is pixel (0, 0).
			viewProjection.TryProjectToScreen(new Vector3(-8f, 4.5f, -20f), Viewport, correction, out var corner, out _).ShouldBeTrue(name);
			corner.X.ShouldBe(0f, 1e-3f, name);
			corner.Y.ShouldBe(0f, 1e-3f, name);
		}
	}
}
