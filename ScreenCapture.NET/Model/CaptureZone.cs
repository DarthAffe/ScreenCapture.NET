// ReSharper disable MemberCanBePrivate.Global

using System;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a duplicated region on the screen.
/// </summary>
public sealed class CaptureZone<TColor> : ICaptureZone
    where TColor : struct, IColor
{
    #region Properties & Fields

    /// <summary>
    /// Gets the unique id of this <see cref="CaptureZone{T}"/>.
    /// </summary>
    public int Id { get; }

    public Display Display { get; }

    /// <summary>
    /// Gets the x-location of the region on the screen.
    /// </summary>
    public int X { get; internal set; }

    /// <summary>
    /// Gets the y-location of the region on the screen.
    /// </summary>
    public int Y { get; internal set; }

    /// <summary>
    /// Gets the width of the captured region.
    /// </summary>
    public int Width { get; internal set; }

    /// <summary>
    /// Gets the height of the captured region.
    /// </summary>
    public int Height { get; internal set; }

    /// <summary>
    /// Gets the level of downscaling applied to the image of this region before copying to local memory. The calculation is (width and height)/2^downscaleLevel.
    /// </summary>
    public int DownscaleLevel { get; internal set; }

    /// <summary>
    /// Gets the original width of the region (this equals <see cref="Width"/> if <see cref="DownscaleLevel"/> is 0).
    /// </summary>
    public int UnscaledWidth { get; internal set; }

    /// <summary>
    /// Gets the original height of the region (this equals <see cref="Height"/> if <see cref="DownscaleLevel"/> is 0).
    /// </summary>
    public int UnscaledHeight { get; internal set; }

    IScreenImage ICaptureZone.Image => Image;
    public ScreenImage<TColor> Image { get; }

    /// <summary>
    /// Gets or sets if the <see cref="CaptureZone{T}"/> should be automatically updated on every captured frame.
    /// </summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>
    /// Gets if an update for the <see cref="CaptureZone{T}"/> is requested on the next captured frame.
    /// </summary>
    public bool IsUpdateRequested { get; private set; }
    
    #endregion

    #region Events

    /// <summary>
    /// Occurs when the <see cref="CaptureZone{T}"/> is updated.
    /// </summary>
    public event EventHandler? Updated;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="CaptureZone{T}"/> class.
    /// </summary>
    /// <param name="id">The unique id of this <see cref="CaptureZone{T}"/>.</param>
    /// <param name="x">The x-location of the region on the screen.</param>
    /// <param name="y">The y-location of the region on the screen.</param>
    /// <param name="width">The width of the region on the screen.</param>
    /// <param name="height">The height of the region on the screen.</param>
    /// <param name="bytesPerPixel">The number of bytes per pixel.</param>
    /// <param name="downscaleLevel">The level of downscaling applied to the image of this region before copying to local memory.</param>
    /// <param name="unscaledWidth">The original width of the region.</param>
    /// <param name="unscaledHeight">The original height of the region</param>
    /// <param name="buffer">The buffer containing the image data.</param>
    internal CaptureZone(int id, Display display, int x, int y, int width, int height, int downscaleLevel, int unscaledWidth, int unscaledHeight, ScreenImage<TColor> image)
    {
        this.Id = id;
        this.Display = display;
        this.X = x;
        this.Y = y;
        this.Width = width;
        this.Height = height;
        this.UnscaledWidth = unscaledWidth;
        this.UnscaledHeight = unscaledHeight;
        this.DownscaleLevel = downscaleLevel;
        this.Image = image;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Requests to update this <see cref="CaptureZone{T}"/> when the next frame is captured.
    /// Only necessary if <see cref="AutoUpdate"/> is set to <c>false</c>.
    /// </summary>
    public void RequestUpdate() => IsUpdateRequested = true;

    /// <summary>
    /// Marks the <see cref="CaptureZone{T}"/> as updated.
    /// </summary>
    internal void SetUpdated()
    {
        IsUpdateRequested = false;

        Updated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Determines whether this <see cref="CaptureZone{T}"/> equals the given one.
    /// </summary>
    /// <param name="other">The <see cref="CaptureZone{T}"/> to compare.</param>
    /// <returns><c>true</c> if the specified object is equal to the current object; otherwise, <c>false</c>.</returns>
    public bool Equals(CaptureZone<TColor> other) => Id == other.Id;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CaptureZone<TColor> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Id;

    #endregion
}