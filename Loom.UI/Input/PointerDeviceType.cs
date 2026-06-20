namespace Loom.UI;

/// <summary>
/// The kind of device that produced a <see cref="PointerInput"/> — WinUI-shaped and device-neutral so no host
/// (Silk.NET) type leaks into <c>Loom.UI</c> (PlanningUI.md §D5.2). MU2 drives only <see cref="Mouse"/>; pen and
/// touch arrive with the same struct at MU5.
/// </summary>
public enum PointerDeviceType : byte { Mouse, Pen, Touch }
