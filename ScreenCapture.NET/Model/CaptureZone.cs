// ReSharper disable MemberCanBePrivate.Global

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a duplicated region on the screen.
/// </summary>
/// <remarks>
/// Thread-safe, see <see cref="ICaptureZone"/>.
/// </remarks>
public sealed class CaptureZone<TColor> : ICaptureZone
    where TColor : unmanaged, IColor<TColor>
{
    #region Properties & Fields

    private readonly Lock _lock = new();
    private readonly Lock _frontLock = new();

    private ZoneBuffer<TColor> _front;
    private ZoneBuffer<TColor>? _back;

    private long _writtenFrameNumber;

    // Replaced as a whole under the zone lock and read without, so a property never mixes two changes.
    private ZoneLayout _layout;

    // 1 while an update is requested.
    private int _updateRequested;

    // Set under the zone lock, so captures holding the zone in their snapshot skip it.
    private bool _isUnregistered;

    /// <inheritdoc />
    public Display Display => Volatile.Read(ref _layout).Display;

    /// <inheritdoc />
    public bool IsValid => Volatile.Read(ref _layout).IsValid;

    /// <inheritdoc />
    public IColorFormat ColorFormat
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => TColor.ColorFormat;
    }

    /// <inheritdoc />
    public CaptureRegion Region => Volatile.Read(ref _layout).Region;

    /// <inheritdoc />
    public int Width => Volatile.Read(ref _layout).Width;

    /// <inheritdoc />
    public int Height => Volatile.Read(ref _layout).Height;

    /// <inheritdoc />
    public int Stride
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Width * ColorFormat.BytesPerPixel;
    }

    /// <inheritdoc />
    public int DownscaleLevel => Volatile.Read(ref _layout).DownscaleLevel;

    /// <inheritdoc />
    public bool AutoUpdate
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    } = true;

    /// <inheritdoc />
    public bool IsUpdateRequested => Volatile.Read(ref _updateRequested) != 0;

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
    /// <param name="region">The region on the display.</param>
    /// <param name="width">The width of the captured pixels (after downscaling).</param>
    /// <param name="height">The height of the captured pixels (after downscaling).</param>
    /// <param name="downscaleLevel">The level of downscaling applied to the image of this region before copying to local memory.</param>
    internal CaptureZone(Display display, CaptureRegion region, int width, int height, int downscaleLevel)
    {
        this._layout = new ZoneLayout(display, region, width, height, downscaleLevel, true);

        _front = new ZoneBuffer<TColor>(width, height);
    }

    #endregion

    #region Methods

    /// <inheritdoc cref="ICaptureZone.AcquireFrame" />
    /// <example>
    /// <code>
    /// using (CaptureFrame&lt;ColorBGRA&gt; frame = zone.AcquireFrame())
    /// {
    ///     ReadOnlySpan&lt;ColorBGRA&gt; pixels = frame.Pixels;
    ///     // ...
    /// }
    /// </code>
    /// </example>
    public CaptureFrame<TColor> AcquireFrame() => new(AddFrontReader());

    /// <inheritdoc />
    CaptureFrame ICaptureZone.AcquireFrame() => new(AddFrontReader());

    private ZoneBuffer<TColor> AddFrontReader()
    {
        lock (_frontLock)
        {
            _front.AddReader();
            return _front;
        }
    }

    /// <summary>
    /// Locks the zone against changes and concurrent captures. Frames don't take this lock.
    /// </summary>
    internal Lock.Scope EnterUpdateScope() => _lock.EnterScope();

    /// <summary>
    /// Gets the buffer the capture writes, sized for the current zone. Called under <see cref="EnterUpdateScope"/>, always followed by <see cref="EndWrite"/>.
    /// </summary>
    /// <returns>The buffer, or <c>null</c> if a frame still reads the back buffer.</returns>
    internal ZoneBuffer<TColor>? BeginWrite()
    {
        ZoneLayout layout = _layout;

        // _back isn't the front anymore, so no new frame can start reading it.
        if (_back?.IsInUse == true) return null;

        if ((_back == null) || !_back.Fits(layout.Width, layout.Height))
            _back = new ZoneBuffer<TColor>(layout.Width, layout.Height);

        return _back;
    }

    /// <summary>
    /// Finishes a write started with <see cref="BeginWrite"/>.
    /// </summary>
    /// <param name="buffer">The buffer returned by <see cref="BeginWrite"/>.</param>
    /// <param name="written"><c>true</c> if the buffer was filled and is shown from now on.</param>
    internal void EndWrite(ZoneBuffer<TColor> buffer, bool written)
    {
        if (!written) return;

        lock (_frontLock)
        {
            _back = _front;
            _front = buffer;
        }
    }

    /// <inheritdoc />
    public void RequestUpdate() => Volatile.Write(ref _updateRequested, 1);

    /// <summary>
    /// Takes the update request before the zone is written, so a request made during the write stays. Called under <see cref="EnterUpdateScope"/>.
    /// </summary>
    /// <returns><c>true</c> if an update was requested.</returns>
    internal bool TakeUpdateRequest() => Interlocked.Exchange(ref _updateRequested, 0) != 0;

    /// <summary>
    /// Checks if the zone has to be written with the given frame.
    /// </summary>
    /// <param name="frameNumber">The number of the frame the capture holds.</param>
    internal bool NeedsUpdate(long frameNumber) => IsValid && (IsUpdateRequested || (AutoUpdate && (Volatile.Read(ref _writtenFrameNumber) != frameNumber)));

    /// <summary>
    /// Records that the zone was written with the given frame. Called under <see cref="EnterUpdateScope"/>.
    /// </summary>
    internal void SetWritten(long frameNumber) => Volatile.Write(ref _writtenFrameNumber, frameNumber);

    /// <summary>
    /// Gets if the zone was unregistered from its capture. Read under <see cref="EnterUpdateScope"/>.
    /// </summary>
    internal bool IsUnregistered => _isUnregistered;

    /// <summary>
    /// Marks the zone as unregistered. Called under <see cref="EnterUpdateScope"/>.
    /// </summary>
    internal void SetUnregistered() => _isUnregistered = true;

    internal void RaiseUpdated()
    {
        try
        {
            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch { /* that's not our fault ... */ }
    }

    /// <summary>
    /// Changes the region of the zone. Called under <see cref="EnterUpdateScope"/> with a region validated against <see cref="Display"/>.
    /// </summary>
    internal void Update(CaptureRegion region, int width, int height, int downscaleLevel)
    {
        ZoneLayout layout = _layout;
        if ((region == layout.Region) && (width == layout.Width) && (height == layout.Height) && (downscaleLevel == layout.DownscaleLevel)) return;

        Volatile.Write(ref _layout, new ZoneLayout(layout.Display, region, width, height, downscaleLevel, region.IsWithin(layout.Display.Width, layout.Display.Height)));
        Volatile.Write(ref _writtenFrameNumber, 0);
    }

    /// <summary>
    /// Moves the zone to the changed display and checks if the region still fits. Called under <see cref="EnterUpdateScope"/>.
    /// </summary>
    internal void SetDisplay(Display display)
    {
        ZoneLayout layout = _layout;
        if (display == layout.Display) return;

        Volatile.Write(ref _layout, layout with { Display = display, IsValid = layout.Region.IsWithin(display.Width, display.Height) });
        Volatile.Write(ref _writtenFrameNumber, 0);
    }

    #endregion

    private sealed record ZoneLayout(Display Display, CaptureRegion Region, int Width, int Height, int DownscaleLevel, bool IsValid);
}