// ReSharper disable MemberCanBePrivate.Global

namespace ScreenCapture.NET;

/// <summary>
/// Represents a graphics-card.
/// </summary>
/// <remarks>
/// Two values are only equal if all their properties are. To find the same graphics card again, compare the <see cref="Id"/>.
/// </remarks>
/// <param name="Id">
/// The id of the graphics card. It identifies the card across restarts of the application and the system, so it can be stored in settings
/// and the card found again with <see cref="ScreenCaptureServiceExtensions.TryGetGraphicsCard"/>.
/// <para>
/// On Windows it's the device interface path of the adapter (<c>\\?\PCI#VEN_10DE&amp;DEV_2684&amp;...</c>). It contains the slot, so two identical cards have different ids;
/// moving the card to another slot changes it. Adapters Windows has no path for (e.g. software renderers) use their LUID, which changes with every restart of the system.
/// The DirectX 11 and the DirectX 9 backend report the same ids.
/// </para>
/// <para>On X11 it's the name of the X display (e.g. <c>:0</c>).</para>
/// </param>
/// <param name="Index">The index of the graphics card in the backend. It can change when devices are added or removed.</param>
/// <param name="Name">The name of the graphics card.</param>
/// <param name="VendorId">The PCI vendor-id of the graphics card (0 if unknown).</param>
/// <param name="DeviceId">The PCI device-id of the graphics card (0 if unknown).</param>
public readonly record struct GraphicsCard(string Id, int Index, string Name, int VendorId, int DeviceId);
