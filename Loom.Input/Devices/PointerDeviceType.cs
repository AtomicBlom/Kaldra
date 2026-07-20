namespace Loom.Input;

/// <summary>
/// The physical device behind a pointer event. A single pointer stream can carry a mouse, a pen/stylus, or a
/// touch contact; the type lets a consumer treat them differently (e.g. hover only makes sense for a mouse).
/// </summary>
public enum PointerDeviceType
{
	Unknown = 0,
	Mouse,
	Pen,
	Touch,
}
