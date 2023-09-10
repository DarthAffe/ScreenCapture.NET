using System;

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
    ColorFormat ColorFormat { get; }

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
    /// Gets the raw buffer of this zone.
    /// </summary>
    ReadOnlySpan<byte> RawBuffer { get; }

    /// <summary>
    /// Gets the <see cref="IImage"/> represented by the buffer of this zone.
    /// </summary>
    IImage Image { get; }

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
    /// Locks the image for use. Unlock by disposing the returned disposable.
    /// </summary>
    /// <returns>The disposable used to unlock the image.</returns>
    IDisposable Lock();

    /// <summary>
    /// Requests to update this <see cref="ICaptureZone"/> when the next frame is captured.
    /// Only necessary if <see cref="AutoUpdate"/> is set to <c>false</c>.
    /// </summary>
    void RequestUpdate();

    /// <summary>
    /// Gets a <see cref="RefImage{TColor}"/>. Basically the same as <see cref="Image"/> but with better performance if the color-layout is known.
    /// </summary>
    /// <typeparam name="TColor">The color used by the buffer of this zone.</typeparam>
    /// <returns>The <see cref="RefImage{TColor}"/> representing this zone.</returns>
    RefImage<TColor> GetRefImage<TColor>() where TColor : struct, IColor;
}
