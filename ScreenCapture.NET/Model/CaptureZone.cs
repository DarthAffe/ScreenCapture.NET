// ReSharper disable MemberCanBePrivate.Global

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a duplicated region on the screen.
/// </summary>
public sealed class CaptureZone<TColor> : ICaptureZone
    where TColor : unmanaged, IColor<TColor>
{
    #region Properties & Fields

    private readonly Lock _lock = new();

    /// <inheritdoc />
    public Display Display { get; }

    /// <inheritdoc />
    public IColorFormat ColorFormat
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => TColor.ColorFormat;
    }

    /// <inheritdoc />
    public int X { get; internal set; }

    /// <inheritdoc />
    public int Y { get; internal set; }

    /// <inheritdoc />
    public int Width { get; private set; }

    /// <inheritdoc />
    public int Height { get; private set; }

    /// <inheritdoc />
    public int Stride
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Width * ColorFormat.BytesPerPixel;
    }

    /// <inheritdoc />
    public int DownscaleLevel { get; private set; }

    /// <inheritdoc />
    public int UnscaledWidth { get; private set; }

    /// <inheritdoc />
    public int UnscaledHeight { get; private set; }

    // Only read or replaced with _lock held.
    internal byte[] InternalBuffer { get; private set; }

    /// <inheritdoc />
    public bool AutoUpdate { get; set; } = true;

    /// <inheritdoc />
    public bool IsUpdateRequested { get; private set; }

    #endregion

    #region Events

    /// <inheritdoc />
    public event EventHandler? Updated;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="CaptureZone{T}"/> class.
    /// </summary>
    /// <param name="display">The <see cref="Display"/> this zone is on.</param>
    /// <param name="x">The x-location of the region on the screen.</param>
    /// <param name="y">The y-location of the region on the screen.</param>
    /// <param name="width">The width of the region on the screen.</param>
    /// <param name="height">The height of the region on the screen.</param>
    /// <param name="downscaleLevel">The level of downscaling applied to the image of this region before copying to local memory.</param>
    /// <param name="unscaledWidth">The original width of the region.</param>
    /// <param name="unscaledHeight">The original height of the region.</param>
    internal CaptureZone(Display display, int x, int y, int width, int height, int downscaleLevel, int unscaledWidth, int unscaledHeight)
    {
        this.Display = display;
        this.X = x;
        this.Y = y;
        this.Width = width;
        this.Height = height;
        this.DownscaleLevel = downscaleLevel;
        this.UnscaledWidth = unscaledWidth;
        this.UnscaledHeight = unscaledHeight;

        InternalBuffer = new byte[Stride * Height];
    }

    #endregion

    #region Methods

    /// <summary>
    /// Locks the zone and returns its current pixels. Unlock by disposing the returned frame.
    /// </summary>
    /// <remarks>
    /// While the lock is held, the zone isn't updated by a capture and its position, size and buffer don't change.
    /// Any method of the <see cref="IScreenCapture"/> can be called while holding the lock, including <see cref="IScreenCapture.UpdateCaptureZone"/> for this zone
    /// (the frame keeps showing the zone as it was when it was locked).
    /// Keep the lock short: a capture waits for it.
    /// </remarks>
    /// <example>
    /// <code>
    /// using (CaptureFrame&lt;ColorBGRA&gt; frame = zone.Lock())
    /// {
    ///     ReadOnlySpan&lt;ColorBGRA&gt; pixels = frame.Pixels;
    ///     // ...
    /// }
    /// </code>
    /// </example>
    /// <returns>The frame holding the lock.</returns>
    public CaptureFrame<TColor> Lock()
    {
        Lock.Scope scope = _lock.EnterScope();
        return new CaptureFrame<TColor>(scope, InternalBuffer, Width, Height, Stride);
    }

    /// <inheritdoc />
    CaptureFrame ICaptureZone.Lock()
    {
        Lock.Scope scope = _lock.EnterScope();
        return new CaptureFrame(scope, ColorDispatcher<TColor>.Instance, InternalBuffer, Width, Height, Stride);
    }

    /// <summary>
    /// Locks the zone without creating a frame (for the capture and zone changes).
    /// </summary>
    internal Lock.Scope EnterLockScope() => _lock.EnterScope();

    /// <inheritdoc />
    public void RequestUpdate() => IsUpdateRequested = true;

    internal void ResetUpdateRequest() => IsUpdateRequested = false;

    internal void RaiseUpdated()
    {
        try
        {
            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch { /* that's not our fault ... */ }
    }

    internal void Resize(int width, int height, int downscaleLevel, int unscaledWidth, int unscaledHeight)
    {
        Width = width;
        Height = height;
        DownscaleLevel = downscaleLevel;
        UnscaledWidth = unscaledWidth;
        UnscaledHeight = unscaledHeight;

        int newBufferSize = Stride * Height;
        if (newBufferSize != InternalBuffer.Length)
            InternalBuffer = new byte[newBufferSize];
    }

    #endregion
}