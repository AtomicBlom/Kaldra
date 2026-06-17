using System.Numerics;

using Loom.Core;

namespace Loom.Tests;

/// <summary>The optional CPU frustum cull (M12): AABB transform, frustum extraction, and the Scene cull helper.</summary>
public class FrustumCullTests
{
	// A camera at (0,0,5) looking toward -Z (origin), 60° vertical FOV, square aspect — the neutral convention.
	private static Camera LookAtOrigin() => new(
		Matrix4x4.CreateLookAt(new Vector3(0, 0, 5), Vector3.Zero, Vector3.UnitY),
		Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 100f));

	private static Bounds UnitBox => Bounds.FromCenterExtents(Vector3.Zero, new Vector3(0.5f));

	[Fact]
	public void Translation_moves_the_box_and_keeps_its_size()
	{
		var world = UnitBox.Transform(Matrix4x4.CreateTranslation(10, 0, 0));

		Assert.Equal(new Vector3(10, 0, 0), world.Center);
		Assert.Equal(new Vector3(0.5f, 0.5f, 0.5f), world.Extents);
	}

	[Fact]
	public void Rotation_grows_the_axis_aligned_extents_conservatively()
	{
		// A unit cube spun 45° about Y: its world AABB half-width in X/Z grows to 0.5*(cos45+sin45) ≈ 0.707.
		var world = UnitBox.Transform(Matrix4x4.CreateRotationY(MathF.PI / 4f));

		Assert.Equal(0.7071f, world.Extents.X, 3);
		Assert.Equal(0.5f, world.Extents.Y, 3);
		Assert.Equal(0.7071f, world.Extents.Z, 3);
	}

	[Fact]
	public void A_box_at_the_focus_is_inside_the_frustum()
	{
		var frustum = LookAtOrigin().ExtractFrustum();
		Assert.True(frustum.Intersects(UnitBox));
	}

	[Fact]
	public void A_box_far_to_the_side_is_culled()
	{
		var frustum = LookAtOrigin().ExtractFrustum();
		var offscreen = UnitBox.Transform(Matrix4x4.CreateTranslation(100, 0, 0));
		Assert.False(frustum.Intersects(offscreen));
	}

	[Fact]
	public void A_box_behind_the_camera_is_culled()
	{
		var frustum = LookAtOrigin().ExtractFrustum();
		// The camera sits at z=5 looking toward -Z, so z=20 is well behind it.
		var behind = UnitBox.Transform(Matrix4x4.CreateTranslation(0, 0, 20));
		Assert.False(frustum.Intersects(behind));
	}

	[Fact]
	public void Scene_cull_compacts_only_the_visible_instances()
	{
		var group = new InstancedGroup(new Mesh(0, 36), "instanced");
		var atFocus = Matrix4x4.CreateTranslation(0, 0, 0);
		var offscreen = Matrix4x4.CreateTranslation(100, 0, 0);
		group.Add(atFocus, UnitBox);
		group.Add(offscreen, UnitBox);
		group.Add(atFocus, UnitBox);

		var visible = new Matrix4x4[group.Count];
		var drawn = group.Cull(LookAtOrigin().ExtractFrustum(), visible);

		Assert.Equal(3, group.Count);
		Assert.Equal(2, drawn); // the two at the focus survive; the offscreen one is culled
		Assert.Equal(atFocus, visible[0]);
		Assert.Equal(atFocus, visible[1]);
	}

	[Fact]
	public void Copy_all_takes_every_instance_without_culling()
	{
		var group = new InstancedGroup(new Mesh(0, 36), "instanced");
		group.Add(Matrix4x4.CreateTranslation(100, 0, 0), UnitBox);
		group.Add(Matrix4x4.CreateTranslation(200, 0, 0), UnitBox);

		var all = new Matrix4x4[group.Count];
		Assert.Equal(2, group.CopyAll(all));
	}
}
