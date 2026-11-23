using BinaryVibrance.Kaldra.Input;

namespace BinaryVibrance.Kaldra.Hosting.Web;

/// <summary>
/// The channel a <see cref="WebInputPump"/> feeds translated events into. Matches
/// <c>IHostedApplication.QueueInput</c> so the hosted app's queue plugs straight in.
/// </summary>
internal delegate void InputSink(in InputEvent e);
