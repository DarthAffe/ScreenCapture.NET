using System;

namespace ScreenCapture.NET;

/// <summary>
/// Represents the duplication of a single display.
/// </summary>
public interface IScreenCapture : IDisposable
{
    /// <summary>
    /// Gets the <see cref="Display"/> this capture is duplicating.
    /// </summary>
    Display Display { get; }

    /// <summary>
    /// Gets a value indicating whether the capture was disposed.
    /// </summary>
    /// <remarks>
    /// Can be read from any thread. A service replaces a disposed capture with a new one (see <see cref="IScreenCaptureService.GetScreenCapture"/> and <see cref="ScreenCaptureCache{TCapture,TOptions}"/>).
    /// </remarks>
    bool IsDisposed { get; }

    /// <summary>
    /// Occurs at the end of every <see cref="CaptureScreen"/>, after the zones were updated, whatever the result.
    /// </summary>
    event EventHandler<ScreenCaptureUpdatedEventArgs>? Updated;

    /// <summary>
    /// Gets the most recent error of the screen capture (e.g. why it's <see cref="CaptureResult.Unavailable"/>), or <c>null</c> if none occurred yet.
    /// </summary>
    /// <remarks>
    /// The capture doesn't throw these errors: it recovers from them where it can and reports the outcome with <see cref="CaptureScreen"/>'s result.
    /// This is set as soon as the error occurs and is never reset, so it's still set after the capture recovered.
    /// Exceptions thrown by handlers of the <c>Updated</c>-events of the capture or its zones aren't reported.
    /// </remarks>
    Exception? LastError { get; }

    /// <summary>
    /// Occurs for every error reported by <see cref="LastError"/>, in order.
    /// </summary>
    /// <remarks>
    /// Raised by <see cref="CaptureScreen"/> (on its thread, with no lock of the library held, before <see cref="Updated"/>) for all errors since the previous call.
    /// Errors that occur outside of <see cref="CaptureScreen"/> (when the capture starts or in <see cref="Restart"/>) are raised by the next call,
    /// so a handler added after creating the capture still gets them. If many errors occur without <see cref="CaptureScreen"/> being called, only the latest are kept.
    /// Exceptions thrown by a handler are ignored.
    /// </remarks>
    event EventHandler<ScreenCaptureErrorEventArgs>? ErrorOccurred;

    /// <summary>
    /// Attempts to capture the current frame shown on the <see cref="Display"/> and updates the zones.
    /// </summary>
    /// <remarks>
    /// After <see cref="CaptureResult.NewFrame"/>, every zone with <see cref="ICaptureZone.AutoUpdate"/> (and every zone that requested an update) is written.
    /// After <see cref="CaptureResult.NoChange"/> and <see cref="CaptureResult.Timeout"/>, the last frame is still current: only zones that requested an update
    /// and zones that don't show it yet (registered or changed since, or skipped because a frame held their buffer) are written from it.
    /// After other results no zone is written; requested updates stay until the next frame.
    /// <para>
    /// While the capture isn't running, every call tries to restart it. After a failed restart, the next attempt waits 100 ms,
    /// doubling with every further failure up to 2 s: the call blocks until then (less if the capture is disposed or <see cref="Restart"/> succeeds meanwhile).
    /// A successful restart resets the delay, so a capture that loses its source (e.g. a display mode change) restarts right away.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see cref="CaptureResult.NewFrame"/> if a new frame was captured; otherwise why not.
    /// <see cref="CaptureResult.NoChange"/> and <see cref="CaptureResult.Timeout"/> are normal when the screen doesn't change.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The screen capture is disposed.</exception>
    CaptureResult CaptureScreen();

    /// <summary>
    /// Creates a new <see cref="ICaptureZone"/> for this <see cref="IScreenCapture"/>.
    /// </summary>
    /// <param name="region">
    /// The region of the display to capture: <see cref="CaptureRegion.X"/> and <see cref="CaptureRegion.Y"/> must be &gt;= 0,
    /// <see cref="CaptureRegion.Width"/> and <see cref="CaptureRegion.Height"/> &gt; 0, and the region must lie within the display.
    /// </param>
    /// <param name="downscaleLevel">
    /// The level of downscaling applied to the image of this region before copying to local memory (must be &gt;= 0). The calculation is (width and height)/2^downscaleLevel.
    /// Levels the region is too small for are reduced, see <see cref="ICaptureZone.DownscaleLevel"/>.
    /// </param>
    /// <param name="doubleBuffered">
    /// <c>true</c> (default) to let the capture write a second buffer while frames of the zone are held (see <see cref="ICaptureZone.Lock"/>);
    /// <c>false</c> to make it wait for them instead, which saves the memory of the second buffer for very large zones.
    /// </param>
    /// <returns>The new <see cref="ICaptureZone"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="region"/> is empty or doesn't lie within the display, or <paramref name="downscaleLevel"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The screen capture is disposed.</exception>
    ICaptureZone RegisterCaptureZone(CaptureRegion region, int downscaleLevel = 0, bool doubleBuffered = true);

    /// <summary>
    /// Removes the given <see cref="ICaptureZone"/> from the <see cref="IScreenCapture"/>.
    /// </summary>
    /// <param name="captureZone">The previously registered <see cref="ICaptureZone"/>.</param>
    /// <returns><c>true</c> if the <see cref="ICaptureZone"/> was successfully removed; otherwise (e.g. it was already removed), <c>false</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="captureZone"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="captureZone"/> can't belong to this screen capture (e.g. it has another color format).</exception>
    /// <exception cref="ObjectDisposedException">The screen capture is disposed.</exception>
    bool UnregisterCaptureZone(ICaptureZone captureZone);

    /// <summary>
    /// Updates the given <see cref="ICaptureZone"/>.
    /// </summary>
    /// <remarks>
    /// <c>null</c>-parameters are ignored and not changed. To change a single value of the region, use <c>with</c>:
    /// <code>
    /// screenCapture.UpdateCaptureZone(zone, zone.Region with { X = 100 });
    /// </code>
    /// </remarks>
    /// <param name="captureZone">The previously registered <see cref="ICaptureZone"/>.</param>
    /// <param name="region">The new region of the display to capture (same rules as for <see cref="RegisterCaptureZone"/>).</param>
    /// <param name="downscaleLevel">The new level of downscaling applied to the image of this region before copying to local memory (must be &gt;= 0). The calculation is (width and height)/2^downscaleLevel.</param>
    /// <exception cref="ArgumentNullException"><paramref name="captureZone"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="captureZone"/> isn't registered to this screen capture.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="region"/> is empty or doesn't lie within the display, or <paramref name="downscaleLevel"/> is negative. The zone isn't changed.</exception>
    /// <exception cref="ObjectDisposedException">The screen capture is disposed.</exception>
    void UpdateCaptureZone(ICaptureZone captureZone, CaptureRegion? region = null, int? downscaleLevel = null);

    /// <summary>
    /// Restarts the <see cref="IScreenCapture"/>.
    /// </summary>
    /// <remarks>
    /// The restart is attempted right away, also while <see cref="CaptureScreen"/> waits for its next attempt.
    /// A failed restart doesn't throw: the error is reported with <see cref="LastError"/> (and <see cref="ErrorOccurred"/>),
    /// and <see cref="CaptureScreen"/> tries again after the backoff (see <see cref="CaptureScreen"/>), returning <see cref="CaptureResult.Unavailable"/> while it keeps failing.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The screen capture is disposed.</exception>
    void Restart();
}
