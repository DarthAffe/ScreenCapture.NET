// ReSharper disable MemberCanBePrivate.Global

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace ScreenCapture.NET;

/// <summary>
/// Represents the information supplied with an <see cref="IScreenCapture.Updated" />-event.
/// </summary>
/// <param name="Result">The result of the capture that raised the event.</param>
/// <param name="FrameNumber">The number of the latest captured frame, starting at 1. 0 if no frame was captured yet.</param>
/// <param name="Timestamp">
/// When the latest captured frame was shown, as a <see cref="Stopwatch"/> timestamp. 0 if no frame was captured yet.
/// DX11 and WGC report when it was presented, Wayland the time the compositor sends (or when it was read), DX9 and X11 when it was read.
/// </param>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Event argument; a struct can't derive from EventArgs.")]
public readonly record struct ScreenCaptureUpdatedEventArgs(CaptureResult Result, long FrameNumber, long Timestamp);