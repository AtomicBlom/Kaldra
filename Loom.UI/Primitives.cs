using System.Numerics;

namespace Loom.UI;

/// <summary>A width/height pair in device-independent pixels (DIPs). Either component may be
/// <see cref="float.PositiveInfinity"/> during measure (an unconstrained axis). Mirrors WinUI's <c>Size</c>.</summary>
public readonly record struct Size(float Width, float Height)
{
	public static Size Zero => new(0f, 0f);
	public static Size Infinite => new(float.PositiveInfinity, float.PositiveInfinity);
	public override string ToString() => $"{Width:0.###}x{Height:0.###}";
}

/// <summary>A point in DIPs (layout-space). Distinct from <see cref="Vector2"/> only for readability at call sites.</summary>
public readonly record struct Point(float X, float Y)
{
	public Vector2 ToVector2() => new(X, Y);
}

/// <summary>Per-edge spacing in DIPs — used for <c>Margin</c>, <c>Padding</c>, and border thickness (WinUI <c>Thickness</c>).</summary>
public readonly record struct Thickness(float Left, float Top, float Right, float Bottom)
{
	public Thickness(float uniform) : this(uniform, uniform, uniform, uniform) { }
	public Thickness(float horizontal, float vertical) : this(horizontal, vertical, horizontal, vertical) { }
	public static Thickness Zero => new(0f);

	/// <summary>Total horizontal inset (<see cref="Left"/> + <see cref="Right"/>).</summary>
	public float Horizontal => Left + Right;

	/// <summary>Total vertical inset (<see cref="Top"/> + <see cref="Bottom"/>).</summary>
	public float Vertical => Top + Bottom;

	public override string ToString() => $"({Left:0.###},{Top:0.###},{Right:0.###},{Bottom:0.###})";
}

/// <summary>WinUI alignment within a layout slot's horizontal axis. <see cref="Stretch"/> fills the slot.</summary>
public enum HorizontalAlignment { Left, Center, Right, Stretch }

/// <summary>WinUI alignment within a layout slot's vertical axis. <see cref="Stretch"/> fills the slot.</summary>
public enum VerticalAlignment { Top, Center, Bottom, Stretch }

/// <summary>WinUI element visibility. <see cref="Collapsed"/> contributes zero to layout and never hit-tests.</summary>
public enum Visibility { Visible, Collapsed }

/// <summary>The axis a <see cref="Controls.StackPanel"/> stacks along.</summary>
public enum Orientation { Vertical, Horizontal }

/// <summary>How a <see cref="GridLength"/> is interpreted: a fixed DIP size, content-sized, or a proportional star.</summary>
public enum GridUnitType { Pixel, Auto, Star }

/// <summary>A Grid row/column track length — fixed pixels, <c>Auto</c> (max child desired), or a <c>*</c> weight
/// of the leftover space (WinUI <c>GridLength</c>). Default is <c>1*</c>, matching WinUI.</summary>
public readonly record struct GridLength(float Value, GridUnitType Type)
{
	public static GridLength Auto => new(0f, GridUnitType.Auto);
	public static GridLength Star => new(1f, GridUnitType.Star);
	public static GridLength Pixels(float v) => new(v, GridUnitType.Pixel);
	public static GridLength Stars(float weight) => new(weight, GridUnitType.Star);

	public bool IsStar => Type == GridUnitType.Star;
	public bool IsAuto => Type == GridUnitType.Auto;
	public bool IsPixel => Type == GridUnitType.Pixel;

	public override string ToString() => Type switch
	{
		GridUnitType.Pixel => $"{Value:0.###}px",
		GridUnitType.Auto => "Auto",
		_ => $"{Value:0.###}*",
	};
}
