namespace Loom.Core;

/// <summary>
/// The per-frame execution context handed to a pass's <c>.Draw</c>/<c>.Dispatch</c> callback.
/// Callbacks <em>pull</em> their inputs from here rather than capturing them, which is what keeps the
/// graph description frame-independent and cacheable (Planning.md §8).
/// <para>
/// M2 is the describe layer only — there is no execution yet, so this interface is intentionally
/// minimal. The full surface (<c>ctx.Scene</c>, <c>ctx.Camera</c>, <c>ctx.Draw</c>, <c>ctx.FullScreen</c>,
/// <c>ctx.Blit</c>, <c>ctx.Run</c>, <c>ctx.Hover</c> — Planning.md §5) is added by the Execute layer in
/// M3+, when the GPU work and the app-owned <c>Scene</c>/<c>Camera</c> types exist.
/// </para>
/// </summary>
public interface IRenderContext
{
	/// <summary>Seconds since the previous frame.</summary>
	float DeltaTime { get; }
}
