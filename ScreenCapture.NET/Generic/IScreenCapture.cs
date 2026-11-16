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
    /// Occurs when the <see cref="IScreenCapture"/> is updated.
    /// </summary>
    event EventHandler<ScreenCaptureUpdatedEventArgs>? Updated;

    /// <summary>
    /// Attempts to capture the current frame showed on the <see cref="Display"/>.
    /// </summary>
    /// <returns><c>true</c> if the current frame was captured successfully; otherwise, <c>false</c>.</returns>
    bool CaptureScreen();

    /// <summary>
    /// Creates a new <see cref="ICaptureZone"/> for this <see cref="IScreenCapture"/>.
    /// </summary>
    /// <param name="region">
    /// The region of the display to capture: <see cref="CaptureRegion.X"/> and <see cref="CaptureRegion.Y"/> must be &gt;= 0,
    /// <see cref="CaptureRegion.Width"/> and <see cref="CaptureRegion.Height"/> &gt; 0, and the region must lie within the display.
    /// </param>
    /// <param name="downscaleLevel">The level of downscaling applied to the image of this region before copying to local memory. The calculation is (width and height)/2^downscaleLevel.</param>
    /// <param name="doubleBuffered">
    /// <c>true</c> (default) to let the capture write a second buffer while frames of the zone are held (see <see cref="ICaptureZone.Lock"/>);
    /// <c>false</c> to make it wait for them instead, which saves the memory of the second buffer for very large zones.
    /// </param>
    /// <returns>The new <see cref="ICaptureZone"/>.</returns>
    ICaptureZone RegisterCaptureZone(CaptureRegion region, int downscaleLevel = 0, bool doubleBuffered = true);

    /// <summary>
    /// Removes the given <see cref="ICaptureZone"/> from the <see cref="IScreenCapture"/>.
    /// </summary>
    /// <param name="captureZone">The previously registered <see cref="ICaptureZone"/>.</param>
    /// <returns><c>true</c> if the <see cref="ICaptureZone"/> was successfully removed; otherwise, <c>false</c>.</returns>
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
    /// <param name="downscaleLevel">The new level of downscaling applied to the image of this region before copying to local memory. The calculation is (width and height)/2^downscaleLevel.</param>
    void UpdateCaptureZone(ICaptureZone captureZone, CaptureRegion? region = null, int? downscaleLevel = null);

    /// <summary>
    /// Restarts the <see cref="IScreenCapture"/>.
    /// </summary>
    void Restart();
}
