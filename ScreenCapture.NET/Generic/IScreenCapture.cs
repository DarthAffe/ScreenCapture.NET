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
    /// <param name="x">The x-location of the region to capture (must be &gt;= 0 and &lt; screen-width).</param>
    /// <param name="y">The y-location of the region to capture (must be &gt;= 0 and &lt; screen-height).</param>
    /// <param name="width">The width of the region to capture (must be &gt;= 0 and this + x must be &lt;= screen-width).</param>
    /// <param name="height">The height of the region to capture (must be &gt;= 0 and this + y must be &lt;= screen-height).</param>
    /// <param name="downscaleLevel">The level of downscaling applied to the image of this region before copying to local memory. The calculation is (width and height)/2^downscaleLevel.</param>
    /// <param name="doubleBuffered">
    /// <c>true</c> (default) to let the capture write a second buffer while frames of the zone are held (see <see cref="ICaptureZone.Lock"/>);
    /// <c>false</c> to make it wait for them instead, which saves the memory of the second buffer for very large zones.
    /// </param>
    /// <returns>The new <see cref="ICaptureZone"/>.</returns>
    ICaptureZone RegisterCaptureZone(int x, int y, int width, int height, int downscaleLevel = 0, bool doubleBuffered = true);

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
    /// <c>null</c>-parameters are ignored and not changed.
    /// </remarks>
    /// <param name="captureZone">The previously registered <see cref="ICaptureZone"/>.</param>
    /// <param name="x">The new x-location of the region to capture (must be &gt;= 0 and &lt; screen-width).</param>
    /// <param name="y">The new y-location of the region to capture (must be &gt;= 0 and &lt; screen-height).</param>
    /// <param name="width">The width of the region to capture (must be &gt;= 0 and this + x must be &lt;= screen-width).</param>
    /// <param name="height">The new height of the region to capture (must be &gt;= 0 and this + y must be &lt;= screen-height).</param>
    /// <param name="downscaleLevel">The new level of downscaling applied to the image of this region before copying to local memory. The calculation is (width and height)/2^downscaleLevel.</param>
    void UpdateCaptureZone(ICaptureZone captureZone, int? x = null, int? y = null, int? width = null, int? height = null, int? downscaleLevel = null);

    /// <summary>
    /// Restarts the <see cref="IScreenCapture"/>.
    /// </summary>
    void Restart();
}
