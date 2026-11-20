using System.Diagnostics.CodeAnalysis;

namespace ScreenCapture.NET;

/// <summary>
/// Represents the information supplied with a <see cref="WaylandScreenCaptureService.RestoreTokenChanged" />-event.
/// </summary>
/// <param name="RestoreToken">The token the portal returned for the session that started: pass it to a later session (<see cref="WaylandSessionOptions.RestoreToken"/>) to restore it without the dialog.</param>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "It is the argument of the RestoreTokenChanged event. A struct can't derive from EventArgs, which EventHandler<T> doesn't require anymore.")]
public readonly record struct WaylandRestoreTokenChangedEventArgs(string RestoreToken);
