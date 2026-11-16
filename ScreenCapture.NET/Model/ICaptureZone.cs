using System;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a zone on the screen to be captured.
/// </summary>
public interface ICaptureZone
{
    /// <summary>
    /// Gets the display this zone is on.
    /// </summary>
    Display Display { get; }

    /// <summary>
    /// Gets the color-format used in the buffer of this zone.
    /// </summary>
    IColorFormat ColorFormat { get; }

    /// <summary>
    /// Gets the x-location of the region on the screen.
    /// </summary>
    int X { get; }

    /// <summary>
    /// Gets the y-location of the region on the screen.
    /// </summary>
    int Y { get; }

    /// <summary>
    /// Gets the width of the captured region.
    /// </summary>
    int Width { get; }

    /// <summary>
    /// Gets the height of the captured region.
    /// </summary>
    int Height { get; }

    /// <summary>
    /// Gets the stride of the buffer.
    /// </summary>
    int Stride { get; }

    /// <summary>
    /// Gets the level of downscaling applied to the image of this region before copying to local memory. The calculation is (width and height)/2^downscaleLevel.
    /// </summary>
    int DownscaleLevel { get; }

    /// <summary>
    /// Gets the original width of the region (this equals <see cref="Width"/> if <see cref="DownscaleLevel"/> is 0).
    /// </summary>
    int UnscaledWidth { get; }

    /// <summary>
    /// Gets the original height of the region (this equals <see cref="Height"/> if <see cref="DownscaleLevel"/> is 0).
    /// </summary>
    int UnscaledHeight { get; }

    /// <summary>
    /// Gets or sets if the <see cref="ICaptureZone"/> should be automatically updated on every captured frame.
    /// </summary>
    bool AutoUpdate { get; set; }

    /// <summary>
    /// Gets if an update for the <see cref="ICaptureZone"/> is requested on the next captured frame.
    /// </summary>
    bool IsUpdateRequested { get; }

    /// <summary>
    /// Occurs when the <see cref="ICaptureZone"/> is updated.
    /// </summary>
    event EventHandler? Updated;

    /// <summary>
    /// Locks the zone and returns its current pixels. Unlock by disposing the returned frame.
    /// </summary>
    /// <remarks>
    /// While the lock is held, the zone isn't updated by a capture and its position, size and buffer don't change.
    /// Any method of the <see cref="IScreenCapture"/> can be called while holding the lock, including <see cref="IScreenCapture.UpdateCaptureZone"/> for this zone
    /// (the frame keeps showing the zone as it was when it was locked).
    /// Keep the lock short: a capture waits for it.
    /// </remarks>
    /// <returns>The frame holding the lock.</returns>
    CaptureFrame Lock();

    /// <summary>
    /// Requests to update this <see cref="ICaptureZone"/> when the next frame is captured.
    /// Only necessary if <see cref="AutoUpdate"/> is set to <c>false</c>.
    /// </summary>
    void RequestUpdate();
}
