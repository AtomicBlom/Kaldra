namespace BinaryVibrance.Kaldra.Input;

/// <summary>
/// A layer in the <see cref="InputLayerStack"/>. Every layer is delivered the frame's input unconditionally,
/// top→bottom; a layer decides only what the layers <i>beneath</i> it see, by blocking device families,
/// consuming individual events, or injecting synthetic ones through its <see cref="InputContext"/>. A layer is
/// consumer-agnostic — UI glue, an action map, a scripted sequence, and a debug console are all just layers.
/// </summary>
public interface IInputLayer
{
	/// <summary>
	/// Processes this layer's view of the frame once during resolution. Poll held state via
	/// <see cref="InputContext.Snapshot"/> and inspect the raw stream via <see cref="InputContext.Incoming"/>;
	/// call <see cref="InputContext.Block"/>/<see cref="InputContext.Consume"/>/<see cref="InputContext.Inject"/>
	/// to shape what lower layers receive.
	/// </summary>
	void Process(ref InputContext ctx);
}
