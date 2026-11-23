namespace BinaryVibrance.Kaldra.Input;

/// <summary>
/// The lifecycle phase of a single touch contact. A contact begins with <see cref="Began"/>, reports
/// <see cref="Moved"/> or <see cref="Stationary"/> while held, and finishes with <see cref="Ended"/> (lifted)
/// or <see cref="Canceled"/> (the system took the contact away, e.g. a gesture was recognised).
/// </summary>
public enum TouchPhase
{
	Began = 0,
	Moved,
	Stationary,
	Ended,
	Canceled,
}
