// ReSharper disable MemberCanBePrivate.Global

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace ScreenCapture.NET;

/// <summary>
/// Represents the information supplied with an <see cref="IScreenCapture.Updated" />-event.
/// </summary>
/// <remarks>
/// A struct, so raising the event on every capture allocates nothing.
/// </remarks>
/// <param name="Result">The result of the capture that raised the event.</param>
/// <param name="FrameNumber">
/// The number of the latest captured frame, counting <see cref="CaptureResult.NewFrame"/> results of this screen capture (the first frame is 1).
/// For other results it's the number of the frame captured before. 0 if no frame was captured yet.
/// </param>
/// <param name="Timestamp">
/// When the latest captured frame was shown, as a <see cref="Stopwatch"/> timestamp (e.g. <c>Stopwatch.GetElapsedTime(e.Timestamp)</c> is the age of the frame).
/// DX11 reports when the frame was presented on the display, DX9 and X11 when it was read.
/// For results other than <see cref="CaptureResult.NewFrame"/> it's the timestamp of the frame captured before. 0 if no frame was captured yet.
/// </param>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "It is the argument of the Updated event. A struct can't derive from EventArgs, which EventHandler<T> doesn't require anymore.")]
public readonly record struct ScreenCaptureUpdatedEventArgs(CaptureResult Result, long FrameNumber, long Timestamp);