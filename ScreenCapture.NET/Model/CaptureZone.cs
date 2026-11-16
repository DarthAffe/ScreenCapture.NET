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
    private readonly Lock _frontLock = new();

    private ZoneBuffer _front;
    private ZoneBuffer? _back;

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

    /// <inheritdoc />
    public bool IsDoubleBuffered { get; }

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
    /// <param name="doubleBuffered">Whether the capture writes a second buffer instead of waiting for frames.</param>
    internal CaptureZone(Display display, int x, int y, int width, int height, int downscaleLevel, int unscaledWidth, int unscaledHeight, bool doubleBuffered)
    {
        this.Display = display;
        this.X = x;
        this.Y = y;
        this.Width = width;
        this.Height = height;
        this.DownscaleLevel = downscaleLevel;
        this.UnscaledWidth = unscaledWidth;
        this.UnscaledHeight = unscaledHeight;
        this.IsDoubleBuffered = doubleBuffered;

        _front = new ZoneBuffer(Width, Height, Stride);
    }

    #endregion

    #region Methods

    /// <summary>
    /// Locks the current pixels of the zone. Release them by disposing the returned frame.
    /// </summary>
    /// <remarks>
    /// The pixels of the frame don't change while it's held.
    /// <para>
    /// Double-buffered zones (the default, see <see cref="IsDoubleBuffered"/>): the capture writes a second buffer and never waits for frames.
    /// A frame that's still held when the capture after the next one starts makes the zone skip that capture.
    /// </para>
    /// <para>
    /// Single-buffered zones: the capture waits until the frame is disposed, and <see cref="Lock"/> waits while the capture writes the zone.
    /// The lock is reentrant: a capture on the thread holding the frame writes the zone anyway, and the frame shows the new pixels.
    /// </para>
    /// <para>
    /// Any method of the <see cref="IScreenCapture"/> can be called while holding a frame, including <see cref="IScreenCapture.UpdateCaptureZone"/> for this zone.
    /// The frame keeps the size it was created with. Keep frames short anyway.
    /// </para>
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
    /// <returns>The frame holding the pixels.</returns>
    public CaptureFrame<TColor> Lock()
    {
        if (IsDoubleBuffered)
            return new CaptureFrame<TColor>(AddFrontReader());

        Lock.Scope scope = _lock.EnterScope();
        return new CaptureFrame<TColor>(_front, scope);
    }

    /// <inheritdoc />
    CaptureFrame ICaptureZone.Lock()
    {
        if (IsDoubleBuffered)
            return new CaptureFrame(ColorDispatcher<TColor>.Instance, AddFrontReader());

        Lock.Scope scope = _lock.EnterScope();
        return new CaptureFrame(ColorDispatcher<TColor>.Instance, _front, scope);
    }

    private ZoneBuffer AddFrontReader()
    {
        lock (_frontLock)
        {
            _front.AddReader();
            return _front;
        }
    }

    /// <summary>
    /// Locks the zone against changes and concurrent captures (and against frames of single-buffered zones).
    /// </summary>
    internal Lock.Scope EnterUpdateScope() => _lock.EnterScope();

    /// <summary>
    /// Gets the buffer the capture writes, sized for the current zone.
    /// </summary>
    /// <remarks>
    /// Called under <see cref="EnterUpdateScope"/>. Every call is followed by <see cref="EndWrite"/>.
    /// </remarks>
    /// <returns>The buffer, or <c>null</c> if the zone has to skip this capture because a frame still reads its back buffer.</returns>
    internal ZoneBuffer? BeginWrite()
    {
        if (!IsDoubleBuffered)
        {
            // Frames hold _lock, so nobody reads the buffer now.
            if (!_front.Fits(Width, Height, Stride))
                _front = new ZoneBuffer(Width, Height, Stride);

            return _front;
        }

        // _back stopped being the front when it was swapped out (under _frontLock), so no frame can start reading it anymore.
        if (_back?.IsInUse == true) return null;

        if ((_back == null) || !_back.Fits(Width, Height, Stride))
            _back = new ZoneBuffer(Width, Height, Stride);

        return _back;
    }

    /// <summary>
    /// Finishes a write started with <see cref="BeginWrite"/>. Double-buffered zones show the written buffer from now on.
    /// </summary>
    /// <param name="buffer">The buffer returned by <see cref="BeginWrite"/>.</param>
    /// <param name="written"><c>true</c> if the buffer was filled; otherwise the zone keeps showing its previous pixels (double-buffered only).</param>
    internal void EndWrite(ZoneBuffer buffer, bool written)
    {
        if (!IsDoubleBuffered || !written) return;

        lock (_frontLock)
        {
            _back = _front;
            _front = buffer;
        }
    }

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
    }

    #endregion
}