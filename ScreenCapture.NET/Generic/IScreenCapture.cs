using System;

namespace ScreenCapture.NET;

/// <summary>
/// Represents the duplication of a single display.
/// </summary>
/// <remarks>
/// <b>Thread safety:</b> all members can be used from any thread, also at the same time.
/// <list type="bullet">
/// <item><description>
/// <see cref="CaptureScreen"/> can run on several threads at once: the calls wait for each other where they use the same resources of the backend.
/// Usually a single thread captures, e.g. a <see cref="ScreenCaptureLoop"/>.
/// </description></item>
/// <item><description>
/// Zones can be registered, unregistered and changed while a capture runs. A capture writes a changed zone with its new region (and size) as soon as it's changed.
/// Once <see cref="UnregisterCaptureZone"/> returned, no capture writes the zone anymore (it waits for a capture writing it); a capture that wrote it just before may still raise its <see cref="ICaptureZone.Updated"/>.
/// </description></item>
/// <item><description>
/// <see cref="Restart"/> can be called while a capture runs: that capture doesn't write its zones from the restarted backend's resources before they hold a frame.
/// </description></item>
/// <item><description>
/// The events of the capture and its zones are raised by <see cref="CaptureScreen"/>, on its thread and with no lock of the library held.
/// Handlers can use the capture and its zones (e.g. update a zone, acquire its frame or register another one).
/// </description></item>
/// <item><description>
/// Any member can be called while holding a frame of a zone (see <see cref="ICaptureZone.AcquireFrame"/>), including <see cref="CaptureScreen"/>.
/// Frames never make a capture wait, but a zone whose frames are held too long skips captures.
/// </description></item>
/// <item><description>
/// <see cref="IDisposable.Dispose"/> can be called while other calls run: they finish, and the methods called afterwards throw an <see cref="ObjectDisposedException"/>
/// (the properties and <see cref="ICaptureZone.AcquireFrame"/> keep working). Dispose waits until a running capture stopped using the backend's resources.
/// </description></item>
/// </list>
/// </remarks>
public interface IScreenCapture : IDisposable
{
    /// <summary>
    /// Gets the <see cref="Display"/> this capture is duplicating.
    /// </summary>
    /// <remarks>
    /// The capture reads the display again whenever it (re)starts, e.g. after a change of the resolution, the rotation or the position, and finds it by its <see cref="Display.Id"/>.
    /// So this can differ from the display the capture was created with; the <see cref="Display.Id"/> stays the same. See <see cref="DisplayChanged"/>.
    /// </remarks>
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
    /// Occurs when the capture found that its <see cref="Display"/> changed (e.g. its resolution, rotation or position).
    /// </summary>
    /// <remarks>
    /// Raised by <see cref="CaptureScreen"/> (on its thread, with no lock of the library held) before the zones are written,
    /// so a handler can fit the zones to the new display with <see cref="UpdateCaptureZone"/> and the same call writes them.
    /// Zones that don't lie within the new display anymore are invalid (see <see cref="ICaptureZone.IsValid"/>) and aren't written until they're updated.
    /// <para>
    /// Changes found outside of <see cref="CaptureScreen"/> (when the capture starts or in <see cref="Restart"/>) are raised by the next call.
    /// Several changes between two calls are raised once, from the display before the first change to the current one; if the display changed back, nothing is raised.
    /// Exceptions thrown by a handler are ignored.
    /// </para>
    /// </remarks>
    event EventHandler<ScreenCaptureDisplayChangedEventArgs>? DisplayChanged;

    /// <summary>
    /// Attempts to capture the current frame shown on the <see cref="Display"/> and updates the zones.
    /// </summary>
    /// <remarks>
    /// After <see cref="CaptureResult.NewFrame"/>, every zone with <see cref="ICaptureZone.AutoUpdate"/> (and every zone that requested an update) is written.
    /// After <see cref="CaptureResult.NoChange"/> and <see cref="CaptureResult.Timeout"/>, the last frame is still current: only zones that requested an update
    /// and zones that don't show it yet (registered or changed since, or skipped because a frame held their buffer) are written from it.
    /// After other results no zone is written; requested updates stay until the next frame. Invalid zones (see <see cref="ICaptureZone.IsValid"/>) are never written.
    /// <para>
    /// While the capture isn't running, every call tries to restart it. After a failed restart, the next attempt waits 100 ms,
    /// doubling with every further failure up to 2 s: the call blocks until then (less if the capture is disposed or <see cref="Restart"/> succeeds meanwhile).
    /// A successful restart resets the delay, so a capture that loses its source (e.g. a display mode change) restarts right away.
    /// A restart reads the display again and raises <see cref="DisplayChanged"/> if it changed.
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
    /// <returns>The new <see cref="ICaptureZone"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="region"/> is empty or doesn't lie within the display, or <paramref name="downscaleLevel"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The screen capture is disposed.</exception>
    ICaptureZone RegisterCaptureZone(CaptureRegion region, int downscaleLevel = 0);

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
    /// <param name="region">The new region of the display to capture (same rules as for <see cref="RegisterCaptureZone"/>). An update with a region that lies within the display makes an invalid zone valid again.</param>
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
    /// It reads the display again (see <see cref="Display"/>); zones that don't fit a changed display are invalid when this returns.
    /// A failed restart doesn't throw: the error is reported with <see cref="LastError"/> (and <see cref="ErrorOccurred"/>),
    /// and <see cref="CaptureScreen"/> tries again after the backoff (see <see cref="CaptureScreen"/>), returning <see cref="CaptureResult.Unavailable"/> while it keeps failing.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The screen capture is disposed.</exception>
    void Restart();
}
