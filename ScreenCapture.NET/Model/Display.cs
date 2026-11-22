using HPPH;

// ReSharper disable MemberCanBePrivate.Global

namespace ScreenCapture.NET;

/// <summary>
/// Represents a display connected to a graphics card.
/// </summary>
/// <remarks>
/// Two values are only equal if all their properties are. To find the same display again (e.g. after a resolution change), compare the <see cref="Id"/>.
/// </remarks>
/// <param name="Id">
/// The id of the display. On Windows and X11 it's stable across restarts, so it can be stored in settings and found again with <see cref="ScreenCaptureServiceExtensions.TryGetDisplay"/>.
/// <para>
/// On Windows it's the device interface path of the monitor (<c>\\?\DISPLAY#IVM6114#...</c>), the same for all Windows backends. It contains the model and the port,
/// so it changes if the monitor is connected to another port. Displays without a path use their <paramref name="DeviceName"/>.
/// A window captured with Windows.Graphics.Capture has the id <c>window:0x</c> followed by its handle.
/// </para>
/// <para>
/// On X11 it's the X display, the screen number and the RandR monitor (e.g. <c>:0/0/DP-1</c>), or <c>:0/0</c> for a screen without RandR monitors.
/// The monitor name is the connector, so it changes if the monitor is connected to another port.
/// </para>
/// <para>
/// On Wayland it's <c>stream:</c> followed by the id the portal gives the stream (portal version 4 and later) or its index (e.g. KDE).
/// It names a stream of the current screen cast session, not the monitor. Only a portal id survives a session restored with its restore token.
/// </para>
/// </param>
/// <param name="Index">The index of the display in the backend. It can change when devices are added or removed.</param>
/// <param name="DeviceName">The device name of the display (e.g. <c>\\.\DISPLAY1</c>; on X11 the RandR monitor, e.g. <c>DP-1</c>; on Wayland e.g. <c>Stream 0</c>).</param>
/// <param name="FriendlyName">The name of the monitor (e.g. its model, as Windows shows it). On X11 the monitor name from its EDID. Empty if unknown (always on Wayland).</param>
/// <param name="X">
/// The x-position of the display on the desktop, in pixels. The primary display is at 0. On X11 the position on the X screen.
/// On Wayland the position in the compositor's space, or 0 if the portal doesn't send it.
/// </param>
/// <param name="Y">
/// The y-position of the display on the desktop, in pixels. The primary display is at 0. On X11 the position on the X screen.
/// On Wayland the position in the compositor's space, or 0 if the portal doesn't send it.
/// </param>
/// <param name="Width">
/// The width of the display, in pixels (rotation already applied).
/// On Wayland it's 0 if the portal doesn't send the size; the <see cref="IScreenCapture.Display"/> of a capture always has it.
/// </param>
/// <param name="Height">The height of the display, in pixels (rotation already applied). See <paramref name="Width"/> for Wayland.</param>
/// <param name="Rotation">
/// How the display is rotated, clockwise. Zones are always in the orientation of the display, a backend capturing the native orientation (DX11) rotates them.
/// Always <see cref="ImageRotation.None"/> on X11 and Wayland, which deliver rotated pixels.
/// Except xdg-desktop-portal-wlr (e.g. sway): it sends rotated monitors unrotated, so they're captured sideways.
/// </param>
/// <param name="GraphicsCard">The <see cref="GraphicsCard"/> this <see cref="Display"/> is connected to.</param>
public readonly record struct Display(string Id, int Index, string DeviceName, string FriendlyName, int X, int Y, int Width, int Height, ImageRotation Rotation, GraphicsCard GraphicsCard);
