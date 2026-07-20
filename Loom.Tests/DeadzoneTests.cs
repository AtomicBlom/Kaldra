using System.Numerics;
using Loom.Input;

namespace Loom.Tests;

/// <summary>Deadzone math: radial (magnitude-gated, direction preserved) and axial (per-component), including
/// the rescale that maps the surviving range back to full deflection.</summary>
public class DeadzoneTests
{
	[Test]
	public void Radial_below_inner_reads_zero()
	{
		Deadzone.Radial(0.2f).Apply(new Vector2(0.1f, 0f)).ShouldBe(Vector2.Zero);
	}

	[Test]
	public void Radial_rescales_magnitude_and_preserves_direction()
	{
		// magnitude 0.6 with inner 0.2, outer 1 → (0.6-0.2)/0.8 = 0.5 along +X.
		var result = Deadzone.Radial(0.2f, 1f).Apply(new Vector2(0.6f, 0f));
		result.X.ShouldBe(0.5f, 0.0001);
		result.Y.ShouldBe(0f, 0.0001);
	}

	[Test]
	public void Radial_preserves_diagonal_direction()
	{
		var input = Vector2.Normalize(new Vector2(1f, 1f)) * 0.6f; // magnitude 0.6 on the diagonal
		var result = Deadzone.Radial(0.2f, 1f).Apply(input);
		result.Length().ShouldBe(0.5f, 0.0001);
		// direction unchanged: equal components
		result.X.ShouldBe(result.Y, 0.0001);
	}

	[Test]
	public void Axial_gates_each_component_independently()
	{
		// X below inner → 0; Y above → (0.6-0.2)/0.8 = 0.5.
		var result = Deadzone.Axial(0.2f, 1f).Apply(new Vector2(0.1f, 0.6f));
		result.X.ShouldBe(0f, 0.0001);
		result.Y.ShouldBe(0.5f, 0.0001);
	}

	[Test]
	public void Scalar_gates_and_rescales_preserving_sign()
	{
		var deadzone = Deadzone.Radial(0.25f, 1f);
		deadzone.Apply(0.1f).ShouldBe(0f);
		deadzone.Apply(-0.625f).ShouldBe(-0.5f, 0.0001); // (0.625-0.25)/0.75 = 0.5, sign kept
	}

	[Test]
	public void None_passes_through_unchanged()
	{
		Deadzone.None.Apply(new Vector2(0.05f, -0.9f)).ShouldBe(new Vector2(0.05f, -0.9f));
		Deadzone.None.Apply(0.05f).ShouldBe(0.05f);
	}
}
