using System;

namespace ScreenCapture.NET;

/// <summary>
/// Represents the duplication of a single display.
/// </summary>
/// <remarks>
/// <b>Thread safety:</b> all members can be used from any thread, also at the same time.
/// <list type="bullet">
/// <item><description>
/// <see cref="CaptureScreen"/> can run on several threads at once. Usually a single thread captures, e.g. a <see cref="ScreenCaptureLoop"/>.
/// </description></item>
/// <item><description>
/// Zones can be registered, unregistered and changed while capturing. Once <see cref="UnregisterCaptureZone"/> returned, the zone isn't written anymore.
/// </description></item>
/// <item><description>
/// <see cref="Restart"/> can be called while capturing.
/// </description></item>
/// <item><description>
/// All events are raised by <see cref="CaptureScreen"/> on its thread, without a lock of the library held. Handlers can use the capture and its zones.
/// </description></item>
/// <item><description>
/// Any member can be called while holding a frame of a zone (see <see cref="ICaptureZone.AcquireFrame"/>). Frames never block a capture,
/// but a zone skips captures while its frames are held.
/// </description></item>
/// <item><description>
/// <see cref="IDisposable.Dispose"/> can be called while other calls run. It waits until a running capture is done with the backend's resources.
/// Methods called afterwards throw an <see cref="ObjectDisposedException"/>; properties and <see cref="ICaptureZone.AcquireFrame"/> keep working.
/// </description></item>
/// </list>
/// </remarks>
public interface IScreenCapture : IDisposable
{
    /// <summary>
    /// Gets the <see cref="Display"/> this capture is duplicating.
    /// </summary>
    /// <remarks>
    /// Read again on every (re)start, so it can differ from the display the capture was created with. The <see cref="Display.Id"/> stays the same.
    /// See <see cref="DisplayChanged"/>.
    /// </remarks>
    Display Display { get; }

    /// <summary>
    /// Gets a value indicating whether the capture was disposed.
    /// </summary>
    /// <remarks>
    /// A service replaces a disposed capture with a new one (see <see cref="IScreenCaptureService.GetScreenCapture"/>).
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
    /// These errors aren't thrown: the capture recovers where it can and reports the outcome as the result of <see cref="CaptureScreen"/>.
    /// Never reset, so it stays set after the capture recovered. Exceptions of event handlers aren't reported.
    /// </remarks>
    Exception? LastError { get; }

    /// <summary>
    /// Occurs for every error reported by <see cref="LastError"/>, in order.
    /// </summary>
    /// <remarks>
    /// Raised by <see cref="CaptureScreen"/> (before <see cref="Updated"/>) for all errors since the previous call, including those of the start and of <see cref="Restart"/>.
    /// Only the latest are kept if <see cref="CaptureScreen"/> isn't called for a while. Exceptions thrown by a handler are ignored.
    /// </remarks>
    event EventHandler<ScreenCaptureErrorEventArgs>? ErrorOccurred;

    /// <summary>
    /// Occurs when the <see cref="Display"/> changed (e.g. its resolution, rotation or position).
    /// </summary>
    /// <remarks>
    /// Raised by <see cref="CaptureScreen"/> before the zones are written, so a handler can fit them with <see cref="UpdateCaptureZone"/> for the same capture.
    /// Zones that don't lie within the new display anymore are invalid (see <see cref="ICaptureZone.IsValid"/>) until they're updated.
    /// <para>
    /// Changes found at the start or in <see cref="Restart"/> are raised by the next call. Several changes between two calls are raised as one;
    /// if the display changed back, nothing is raised. Exceptions thrown by a handler are ignored.
    /// </para>
    /// </remarks>
    event EventHandler<ScreenCaptureDisplayChangedEventArgs>? DisplayChanged;

    /// <summary>
    /// Attempts to capture the current frame shown on the <see cref="Display"/> and updates the zones.
    /// </summary>
    /// <remarks>
    /// After <see cref="CaptureResult.NewFrame"/> all zones with <see cref="ICaptureZone.AutoUpdate"/> and all zones that requested an update are written.
    /// After <see cref="CaptureResult.NoChange"/> and <see cref="CaptureResult.Timeout"/> only zones that requested an update or don't show the last frame yet are written.
    /// After other results no zone is written. Invalid zones are never written.
    /// <para>
    /// While the capture isn't running, every call tries to restart it. After a failed restart the next attempt waits 100 ms, doubling up to 2 s;
    /// the call blocks until then. A successful restart resets the delay.
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
    /// <param name="region">The region of the display to capture. It must not be empty and must lie within the display.</param>
    /// <param name="downscaleLevel">
    /// The level of downscaling applied to the image of this region before copying to local memory (must be &gt;= 0). The calculation is (width and height)/2^downscaleLevel.
    /// Levels the region is too small for are reduced, see <see cref="ICaptureZone.DownscaleLevel"/>.
    /// </param>
    /// <returns>The new <see cref="ICaptureZone"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="region"/> is empty or doesn't lie within the display, or <paramref name="downscaleLevel"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The screen capture is disposed.</exception>
    ICaptureZone RegisterCaptureZone(CaptureRegion region, int downscaleLevel = 0);

    /// <summary>
    /// Removes the given <see cref="ICaptureZone"/> from the <see cref="IScreenCapture"/>.
    /// </summary>
    /// <param name="captureZone">The previously registered <see cref="ICaptureZone"/>.</param>
    /// <returns><c>true</c> if the <see cref="ICaptureZone"/> was successfully removed; otherwise, <c>false</c>.</returns>
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
    /// <param name="region">The new region of the display to capture (same rules as for <see cref="RegisterCaptureZone"/>). Makes an invalid zone valid again.</param>
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
    /// Attempted right away, also while <see cref="CaptureScreen"/> waits for its next attempt. Reads the display again;
    /// zones that don't fit a changed display are invalid when this returns.
    /// A failed restart doesn't throw. It's reported with <see cref="LastError"/>, and <see cref="CaptureScreen"/> tries again after the backoff.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The screen capture is disposed.</exception>
    void Restart();
}
