using BinaryVibrance.Kaldra.Input;

namespace BinaryVibrance.Kaldra.Hosting.Desktop;

/// <summary>
/// The channel a <see cref="DesktopInputPump"/> feeds translated events into. Matches
/// <c>IHostedApplication.QueueInput</c> so the app's queue plugs straight in, while a hand-rolled host that owns
/// its own <see cref="InputLayerStack"/> can supply any sink.
/// </summary>
public delegate void InputSink(in InputEvent e);
