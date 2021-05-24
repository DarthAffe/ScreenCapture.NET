// ReSharper disable MemberCanBePrivate.Global
using System;

namespace ScreenCapture
{
    /// <summary>
    /// Represents a duplicated region on the screen.
    /// </summary>
    public sealed class CaptureZone
    {
        #region Properties & Fields

        /// <summary>
        /// Gets the unique id of this <see cref="CaptureZone"/>.
        /// </summary>
        public int Id { get; }

        /// <summary>
        /// Gets the x-location of the region on the screen.
        /// </summary>
        public int X { get; }

        /// <summary>
        /// Gets the y-location of the region on the screen.
        /// </summary>
        public int Y { get; }

        /// <summary>
        /// Gets the width of the region on the screen.
        /// </summary>
        public int Width { get; }

        /// <summary>
        /// Gets the height of the region on the screen.
        /// </summary>
        public int Height { get; }

        /// <summary>
        /// Gets the level of downscaling applied to the image of this region before copying to local memory. The calculation is (width and height)/2^downscaleLevel.
        /// </summary>
        public int DownscaleLevel { get; }

        /// <summary>
        /// Gets the original width of the region (this equals <see cref="Width"/> if <see cref="DownscaleLevel"/> is 0).
        /// </summary>
        public int UnscaledWidth { get; }

        /// <summary>
        /// Gets the original height of the region (this equals <see cref="Height"/> if <see cref="DownscaleLevel"/> is 0).
        /// </summary>
        public int UnscaledHeight { get; }

        /// <summary>
        /// Gets the actually captured width of the region (this can be greated than <see cref="Width"/> due to size-constraints on the GPU).
        /// </summary>
        public int CaptureWidth { get; }

        /// <summary>
        /// Gets the actually captured height of the region (this can be greated than <see cref="Height"/> due to size-constraints on the GPU).
        /// </summary>
        public int CaptureHeight { get; }

        /// <summary>
        /// Gets the width of the buffer the capture is saved to.
        /// Equals <see cref="CaptureWidth"/> most of the time but can be bigger.
        /// </summary>
        public int BufferWidth { get; }

        /// <summary>
        /// Gets the height of the buffer the capture is saved to.
        /// Equals <see cref="CaptureHeight"/> most of the time but can be bigger.
        /// </summary>
        public int BufferHeight { get; }

        /// <summary>
        /// Gets the buffer containing the image data. Format depends on the specific capture but is most likely BGRA32.
        /// </summary>
        public byte[] Buffer { get; }

        /// <summary>
        /// Gets the config for black-bar detection.
        /// </summary>
        public BlackBarDetection BlackBars { get; }

        /// <summary>
        /// Gets or sets if the <see cref="CaptureZone"/> should be automatically updated on every captured frame.
        /// </summary>
        public bool AutoUpdate { get; set; } = true;

        /// <summary>
        /// Gets if an update for the <see cref="CaptureZone"/> is requested on the next captured frame.
        /// </summary>
        public bool IsUpdateRequested { get; private set; }

        #endregion

        #region Events

        /// <summary>
        /// Occurs when the <see cref="CaptureZone"/> is updated.
        /// </summary>
        public event EventHandler? Updated;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="CaptureZone"/> class.
        /// </summary>
        /// <param name="id">The unique id of this <see cref="CaptureZone"/>.</param>
        /// <param name="x">The x-location of the region on the screen.</param>
        /// <param name="y">The y-location of the region on the screen.</param>
        /// <param name="width">The width of the region on the screen.</param>
        /// <param name="height">The height of the region on the screen.</param>
        /// <param name="downscaleLevel">The level of downscaling applied to the image of this region before copying to local memory.</param>
        /// <param name="unscaledWidth">The original width of the region.</param>
        /// <param name="unscaledHeight">The original height of the region</param>
        /// <param name="captureWidth">The actually captured width of the region.</param>
        /// <param name="captureHeight">The actually captured height of the region.</param>
        /// <param name="bufferWidth">The width of the buffer the capture is saved to.</param>
        /// <param name="bufferHeight">The height of the buffer the capture is saved to.</param>
        /// <param name="buffer">The buffer containing the image data.</param>
        public CaptureZone(int id, int x, int y, int width, int height, int downscaleLevel, int unscaledWidth, int unscaledHeight, int captureWidth, int captureHeight, int bufferWidth, int bufferHeight, byte[] buffer)
        {
            this.Id = id;
            this.X = x;
            this.Y = y;
            this.Width = width;
            this.Height = height;
            this.UnscaledWidth = unscaledWidth;
            this.UnscaledHeight = unscaledHeight;
            this.DownscaleLevel = downscaleLevel;
            this.CaptureWidth = captureWidth;
            this.CaptureHeight = captureHeight;
            this.BufferWidth = bufferWidth;
            this.BufferHeight = bufferHeight;
            this.Buffer = buffer;

            BlackBars = new BlackBarDetection(this);
        }

        #endregion

        #region Methods

        /// <summary>
        /// Requests to update this <see cref="CaptureZone"/> when the next frame is captured.
        /// Only necessary if <see cref="AutoUpdate"/> is set to <c>false</c>.
        /// </summary>
        public void RequestUpdate() => IsUpdateRequested = true;

        /// <summary>
        /// Marks the <see cref="CaptureZone"/> as updated.
        /// WARNING: This should not be called outside of an <see cref="IScreenCapture"/>!
        /// </summary>
        public void SetUpdated()
        {
            IsUpdateRequested = false;
            BlackBars.InvalidateCache();

            Updated?.Invoke(this, new EventArgs());
        }

        /// <summary>
        /// Determines whether this <see cref="CaptureZone"/> equals the given one.
        /// </summary>
        /// <param name="other">The <see cref="CaptureZone"/> to compare.</param>
        /// <returns><c>true</c> if the specified object is equal to the current object; otherwise, <c>false</c>.</returns>
        public bool Equals(CaptureZone other) => Id == other.Id;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is CaptureZone other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => Id;

        #endregion
    }
}