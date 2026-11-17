using HPPH;

// ReSharper disable MemberCanBePrivate.Global

namespace ScreenCapture.NET;

/// <summary>
/// Represents a display connected to graphics-card.
/// </summary>
/// <remarks>
/// Two values are only equal if all their properties are, e.g. not after the resolution of the display changed. To find the same display again, compare the <see cref="Id"/>.
/// </remarks>
/// <param name="Id">
/// The id of the display. It identifies the display across restarts of the application and the system, so it can be stored in settings
/// and the display found again with <see cref="ScreenCaptureServiceExtensions.TryGetDisplay"/>.
/// <para>
/// On Windows it's the device interface path of the monitor (<c>\\?\DISPLAY#IVM6114#...</c>). It contains the model and the port, so two identical monitors have different ids;
/// connecting the monitor to another port changes it. Displays Windows has no path for use their <paramref name="DeviceName"/>, which can change when displays are added or removed.
/// The DirectX 11 and the DirectX 9 backend report the same ids.
/// </para>
/// <para>On X11 it's the name of the X display and the screen number (e.g. <c>:0/0</c>).</para>
/// </param>
/// <param name="Index">The index of the display in the backend. It can change when devices are added or removed.</param>
/// <param name="DeviceName">The device name of the display (e.g. <c>\\.\DISPLAY1</c>).</param>
/// <param name="FriendlyName">The name of the monitor (e.g. its model, as Windows shows it in the display settings). Empty if unknown, which is always the case on X11.</param>
/// <param name="X">The x-position of the display on the desktop, in pixels. The primary display is at 0, displays left of it are negative. Always 0 on X11, where every screen has its own coordinates.</param>
/// <param name="Y">The y-position of the display on the desktop, in pixels. The primary display is at 0, displays above it are negative. Always 0 on X11, where every screen has its own coordinates.</param>
/// <param name="Width">The width of the display on the desktop, in pixels (rotation already applied).</param>
/// <param name="Height">The height of the display on the desktop, in pixels (rotation already applied).</param>
/// <param name="Rotation">
/// How the display is rotated, clockwise (on Windows: the orientation set in the display settings).
/// Zones are always in the orientation of the display: a backend that captures the output in its native orientation (DX11) rotates the pixels by this.
/// </param>
/// <param name="GraphicsCard">The <see cref="GraphicsCard"/> this <see cref="Display"/> is connected to.</param>
public readonly record struct Display(string Id, int Index, string DeviceName, string FriendlyName, int X, int Y, int Width, int Height, ImageRotation Rotation, GraphicsCard GraphicsCard);
