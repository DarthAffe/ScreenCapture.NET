using System.Diagnostics.CodeAnalysis;

namespace ScreenCapture.NET;

/// <summary>
/// Represents the information supplied with a <see cref="WaylandScreenCaptureService.RestoreTokenChanged" />-event.
/// </summary>
/// <param name="RestoreToken">The new token (see <see cref="WaylandSessionOptions.RestoreToken"/>).</param>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Event argument; a struct can't derive from EventArgs.")]
public readonly record struct WaylandRestoreTokenChangedEventArgs(string RestoreToken);
