using System.Runtime.InteropServices;
using Avalonia;
using HPPH;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ScreenCapture.NET.Viewer;

/// <summary>
/// Keeps a copy of the latest frame of a zone, so the UI can show it without holding the frame.
/// </summary>
/// <remarks>
/// The handler copies the pixels on the thread of the loop, the UI picks them up with <see cref="UpdateBitmap"/>.
/// </remarks>
internal sealed class ZoneMirror : IDisposable
{
    #region Properties & Fields

    private readonly Lock _lock = new();
    private byte[] _pixels = [];
    private int _width;
    private int _height;
    private int _stride;
    private bool _hasNewFrame;
    private BlackBars? _blackBars;
    private volatile int _blackBarThreshold = -1;

    public ICaptureZone Zone { get; }

    /// <summary>
    /// Gets or sets the threshold of the black bar detection (see <see cref="BlackBarDetection"/>), or -1 to turn it off.
    /// </summary>
    public int BlackBarThreshold
    {
        get => _blackBarThreshold;
        set => _blackBarThreshold = value;
    }

    /// <summary>
    /// Gets the black bars of the latest frame, or <c>null</c> if the detection is off.
    /// </summary>
    public BlackBars? BlackBars
    {
        get
        {
            lock (_lock)
                return _blackBars;
        }
    }

    /// <summary>
    /// Occurs on the thread of the loop when a new frame was copied.
    /// </summary>
    public event Action? FrameReady;

    #endregion

    #region Constructors

    public ZoneMirror(ICaptureZone zone)
    {
        Zone = zone;
        zone.Updated += OnZoneUpdated;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Writes the latest frame into the bitmap, or into a new one if the size changed.
    /// </summary>
    /// <returns>The bitmap that shows the frame, or <c>null</c> if there's no new frame.</returns>
    public WriteableBitmap? UpdateBitmap(WriteableBitmap? bitmap)
    {
        lock (_lock)
        {
            if (!_hasNewFrame) return null;
            _hasNewFrame = false;

            if ((bitmap == null) || (bitmap.PixelSize.Width != _width) || (bitmap.PixelSize.Height != _height))
                bitmap = new WriteableBitmap(new PixelSize(_width, _height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

            using ILockedFramebuffer framebuffer = bitmap.Lock();
            int rowSize = _width * 4;
            for (int y = 0; y < _height; y++)
                Marshal.Copy(_pixels, y * _stride, framebuffer.Address + (y * framebuffer.RowBytes), rowSize);

            return bitmap;
        }
    }

    private void OnZoneUpdated(object? sender, EventArgs e)
    {
        using CaptureFrame frame = Zone.AcquireFrame();
        ReadOnlySpan<byte> pixels = frame.RawBuffer;

        // Detected while the frame is held, on the thread of the loop.
        int threshold = _blackBarThreshold;
        BlackBars? blackBars = null;
        if (threshold >= 0)
        {
            RefImage<ColorBGRA> image = frame.AsRefImage<ColorBGRA>();
            blackBars = new BlackBars(image.CalculateTop(threshold), image.CalculateBottom(threshold), image.CalculateLeft(threshold), image.CalculateRight(threshold), frame.Width, frame.Height);
        }

        lock (_lock)
        {
            if (_pixels.Length != pixels.Length)
                _pixels = new byte[pixels.Length];

            pixels.CopyTo(_pixels);
            _width = frame.Width;
            _height = frame.Height;
            _stride = frame.Stride;
            _blackBars = blackBars;
            _hasNewFrame = true;
        }

        FrameReady?.Invoke();
    }

    public void Dispose() => Zone.Updated -= OnZoneUpdated;

    #endregion
}

/// <summary>
/// The content of a frame without its black bars, as <see cref="BlackBarDetection"/> returns it, in pixels of the frame.
/// </summary>
/// <param name="Top">The first row with content (the height of the top bar).</param>
/// <param name="Bottom">The end of the content (exclusive): the bottom bar is <c>Height - Bottom</c> high.</param>
/// <param name="Left">The first column with content (the width of the left bar).</param>
/// <param name="Right">The end of the content (exclusive): the right bar is <c>Width - Right</c> wide.</param>
/// <param name="Width">The width of the frame.</param>
/// <param name="Height">The height of the frame.</param>
internal readonly record struct BlackBars(int Top, int Bottom, int Left, int Right, int Width, int Height);
