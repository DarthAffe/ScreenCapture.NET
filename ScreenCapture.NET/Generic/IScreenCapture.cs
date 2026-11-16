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
    /// Occurs at the end of every <see cref="CaptureScreen"/>, after the zones were updated, whatever the result.
    /// </summary>
    event EventHandler<ScreenCaptureUpdatedEventArgs>? Updated;

    /// <summary>
    /// Attempts to capture the current frame shown on the <see cref="Display"/> and updates the zones.
    /// </summary>
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
    void Restart();
}
