// ReSharper disable MemberCanBePrivate.Global

namespace ScreenCapture.NET;

/// <summary>
/// Represents a graphics card.
/// </summary>
/// <remarks>
/// Two values are only equal if all their properties are. To find the same graphics card again, compare the <see cref="Id"/>.
/// </remarks>
/// <param name="Id">
/// The id of the graphics card. It's stable across restarts, so it can be stored in settings and found again with <see cref="ScreenCaptureServiceExtensions.TryGetGraphicsCard"/>.
/// <para>
/// On Windows it's the device interface path of the adapter (<c>\\?\PCI#VEN_10DE&amp;DEV_2684&amp;...</c>), the same for DX11 and DX9. It contains the slot,
/// so it changes if the card is moved. Adapters without a path (e.g. software renderers) use their LUID, which changes with every reboot.
/// </para>
/// <para>
/// On X11 it's the name of the X display as it was opened (e.g. <c>:0</c>, or <c>$DISPLAY</c>). Different spellings of the same server (<c>:0</c>, <c>:0.0</c>, <c>unix:0</c>) give different ids.
/// </para>
/// <para>
/// On Wayland there's one graphics card with the id <c>xdg-desktop-portal</c> and the desktop as its name (e.g. <c>KDE</c>), since the portal doesn't tell the card.
/// </para>
/// </param>
/// <param name="Index">The index of the graphics card in the backend. It can change when devices are added or removed.</param>
/// <param name="Name">The name of the graphics card.</param>
/// <param name="VendorId">The PCI vendor-id of the graphics card (0 if unknown).</param>
/// <param name="DeviceId">The PCI device-id of the graphics card (0 if unknown).</param>
public readonly record struct GraphicsCard(string Id, int Index, string Name, int VendorId, int DeviceId);
