using System;

namespace ScreenCapture.NET;

public interface ICaptureZone
{
    /// <summary>
    /// Gets the unique id of this <see cref="ICaptureZone"/>.
    /// </summary>
    int Id { get; }
    Display Display { get; }
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

    ReadOnlySpan<byte> RawBuffer { get; }

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
    /// Requests to update this <see cref="ICaptureZone"/> when the next frame is captured.
    /// Only necessary if <see cref="AutoUpdate"/> is set to <c>false</c>.
    /// </summary>
    void RequestUpdate();
}
