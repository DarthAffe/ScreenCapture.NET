using HPPH;

// ReSharper disable MemberCanBePrivate.Global

namespace ScreenCapture.NET;

/// <summary>
/// Represents a display connected to a graphics card.
/// </summary>
/// <remarks>
/// Two values are only equal if all their properties are, e.g. not after the resolution of the display changed. To find the same display again, compare the <see cref="Id"/>.
/// </remarks>
/// <param name="Id">
/// The id of the display. On Windows and X11 it identifies the display across restarts of the application and the system, so it can be stored in settings
/// and the display found again with <see cref="ScreenCaptureServiceExtensions.TryGetDisplay"/>. On Wayland it's only valid in a screen cast session (see below).
/// <para>
/// On Windows it's the device interface path of the monitor (<c>\\?\DISPLAY#IVM6114#...</c>). It contains the model and the port, so two identical monitors have different ids;
/// connecting the monitor to another port changes it. Displays Windows has no path for use their <paramref name="DeviceName"/>, which can change when displays are added or removed.
/// The DirectX 11, the DirectX 9 and the Windows.Graphics.Capture backend report the same ids.
/// </para>
/// <para>
/// A window captured by the Windows.Graphics.Capture backend has the id <c>window:0x</c> followed by its handle; it's only valid while the window exists.
/// </para>
/// <para>
/// On X11 it's the name of the X display, the screen number and the name of the RandR monitor (e.g. <c>:0/0/DP-1</c>). The monitor name is the connector,
/// so connecting the monitor to another port changes it. A screen of a server without RandR monitors is one display with the id <c>:0/0</c>.
/// The X display is named as it was opened (see <see cref="GraphicsCard.Id"/>).
/// </para>
/// <para>
/// On Wayland it's <c>stream:</c> followed by the id the portal gives the stream (portal version 4 and later), or by the index of the stream if the portal gives none (e.g. KDE).
/// It names a stream of the current screen cast session, i.e. a monitor the user selected, not the monitor itself:
/// the id of the portal is kept by a session restored with its restore token, an index only names a position in the selection, so after a new selection it can be another monitor.
/// </para>
/// </param>
/// <param name="Index">The index of the display in the backend. It can change when devices are added or removed.</param>
/// <param name="DeviceName">The device name of the display (e.g. <c>\\.\DISPLAY1</c>; on X11 the name of the RandR monitor, e.g. <c>DP-1</c>; on Wayland <c>Stream</c> and the index of the stream, e.g. <c>Stream 0</c>).</param>
/// <param name="FriendlyName">The name of the monitor (e.g. its model, as Windows shows it in the display settings). On X11 it's the monitor name from its EDID. Empty if unknown (always on Wayland).</param>
/// <param name="X">
/// The x-position of the display on the desktop, in pixels. The primary display is at 0, displays left of it are negative. On X11 it's the position on the X screen, whose left edge is 0.
/// On Wayland it's the position in the compositor's space, or 0 if the portal doesn't send it.
/// </param>
/// <param name="Y">
/// The y-position of the display on the desktop, in pixels. The primary display is at 0, displays above it are negative. On X11 it's the position on the X screen, whose top edge is 0.
/// On Wayland it's the position in the compositor's space, or 0 if the portal doesn't send it.
/// </param>
/// <param name="Width">
/// The width of the display on the desktop, in pixels (rotation already applied).
/// On Wayland it's 0 if the portal doesn't send the size: the capture reads it from the first frame, and zones can only be registered once it's known.
/// </param>
/// <param name="Height">The height of the display on the desktop, in pixels (rotation already applied). On Wayland it's 0 if the portal doesn't send the size (see <paramref name="Width"/>).</param>
/// <param name="Rotation">
/// How the display is rotated, clockwise (on Windows: the orientation set in the display settings).
/// Zones are always in the orientation of the display: a backend that captures the output in its native orientation (DX11) rotates the pixels by this.
/// Always <see cref="ImageRotation.None"/> on X11, which delivers the pixels already rotated, and on Wayland.
/// </param>
/// <param name="GraphicsCard">The <see cref="GraphicsCard"/> this <see cref="Display"/> is connected to.</param>
public readonly record struct Display(string Id, int Index, string DeviceName, string FriendlyName, int X, int Y, int Width, int Height, ImageRotation Rotation, GraphicsCard GraphicsCard);
