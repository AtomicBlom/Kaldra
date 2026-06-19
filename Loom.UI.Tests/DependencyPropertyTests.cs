namespace Loom.UI.Tests;

/// <summary>
/// MU0 Phase B coverage for the reflection-free dependency-property model + value-precedence ladder
/// (PlanningUI.md §D1 / §D8): resolve <c>Animation &gt; Local &gt; Style &gt; Inherited &gt; Default</c>, fire the
/// typed change callback only on a <em>resolved</em> change, and ensure <c>SetValue</c> never clobbers an active
/// animation. Asserted directly on a probe object — no engine, no GPU.
/// </summary>
public sealed class DependencyPropertyTests
{
	private sealed class Probe : DependencyObject
	{
		public static readonly DependencyProperty<Probe, int> ValueProperty =
			DependencyProperty.Register<Probe, int>("Value",
				new PropertyMetadata<Probe, int> { DefaultValue = -1, PropertyChanged = static (o, ov, nv) => o.Log.Add((ov, nv)) });

		public int Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
		public readonly List<(int Old, int New)> Log = [];
	}

	[Test]
	public void Unset_property_resolves_to_its_typed_default()
	{
		new Probe().Value.ShouldBe(-1);
	}

	[Test]
	public void Local_overrides_default_and_fires_the_typed_change_once()
	{
		var p = new Probe();
		p.Value = 5;
		p.Value.ShouldBe(5);
		p.Log.ShouldHaveSingleItem();
		p.Log[0].ShouldBe((-1, 5));
	}

	[Test]
	public void Setting_the_same_resolved_value_does_not_refire()
	{
		var p = new Probe { Value = 7 };
		p.Log.Clear();
		p.Value = 7;
		p.Log.ShouldBeEmpty();
	}

	[Test]
	public void Local_outranks_style_which_outranks_inherited()
	{
		var p = new Probe();
		p.SetInheritedValue(Probe.ValueProperty, 1);
		p.Value.ShouldBe(1);
		p.SetStyleValue(Probe.ValueProperty, 2);
		p.Value.ShouldBe(2);
		p.Value = 3; // Local
		p.Value.ShouldBe(3);
	}

	[Test]
	public void Animation_outranks_local_and_clearing_it_reverts_to_local()
	{
		var p = new Probe { Value = 5 };
		p.SetAnimatedValue(Probe.ValueProperty, 42);
		p.Value.ShouldBe(42);
		p.ClearAnimatedValue(Probe.ValueProperty);
		p.Value.ShouldBe(5); // Local, never clobbered by the animation
	}
}
