namespace BinaryVibrance.Kaldra.Core.Hosting;

/// <summary>
/// Thrown when a pass binds a resource it never declared — e.g. <c>ctx.FullScreen("blit", hdr)</c> on a pass that
/// forgot <c>.Samples(hdr)</c>. The graph derives ordering, barriers and image layouts purely from the declared
/// access edges, so an undeclared binding is read in whatever layout the previous pass happened to leave: garbage or
/// validation noise in a debug build, and silently wrong pixels in a release one.
/// </summary>
/// <param name="pass">The pass that was recording.</param>
/// <param name="resource">The resource that was bound without being declared.</param>
/// <param name="message">The rest of the message; the <c>Pass '…': </c> prefix is added here.</param>
public sealed class PassBindingException(string pass, string resource, string message)
	: KaldraAuthoringException($"Pass '{pass}': {message}")
{
	/// <summary>The pass that bound the resource.</summary>
	public string Pass { get; } = pass;

	/// <summary>The resource that was bound but never declared on <see cref="Pass"/>.</summary>
	public string Resource { get; } = resource;
}
