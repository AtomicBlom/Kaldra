namespace Loom.Core;

/// <summary>
/// An <em>optional</em>, app-owned draw-list container (PlanningPhase2 §6: the engine offers a <c>Scene</c> and
/// a frustum-cull helper, but never mandates a scene graph or seizes draw submission). A scene is a flat set of
/// <see cref="InstancedGroup"/>s; the app builds it once, culls it each frame against
/// <see cref="Camera.ExtractFrustum"/>, and feeds the surviving transforms to <c>ctx.DrawInstanced</c>. Nothing
/// in the engine requires its use — a demo that draws meshes directly still works.
/// </summary>
public sealed class Scene
{
	private readonly List<InstancedGroup> _groups = [];

	/// <summary>The instanced groups in this scene.</summary>
	public IReadOnlyList<InstancedGroup> Groups => _groups;

	/// <summary>Add and return a new instanced group for <paramref name="mesh"/> drawn with <paramref name="technique"/>.</summary>
	public InstancedGroup AddInstanced(Mesh mesh, string technique)
	{
		var group = new InstancedGroup(mesh, technique);
		_groups.Add(group);
		return group;
	}
}
