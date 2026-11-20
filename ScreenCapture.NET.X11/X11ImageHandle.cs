using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ScreenCapture.NET;

/// <summary>
/// Owns an <c>XImage*</c> (including its pixel data) created with <see cref="X11.XGetImage"/>, or one in shared memory created with <see cref="CreateShared"/>.
/// </summary>
/// <remarks>
/// If <see cref="X11.XGetImage"/> fails, the handle is invalid (<see cref="SafeHandle.IsInvalid"/>) and must not be used.
/// Readers of <see cref="Data"/> have to hold a reference (<see cref="SafeHandle.DangerousAddRef"/>) so the memory isn't freed meanwhile.
/// </remarks>
internal sealed unsafe class X11ImageHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    #region Properties & Fields

    private X11.XImage _image;

    // Only for images in shared memory. Native memory, since the image keeps a pointer to it (obdata).
    private X11.XShmSegmentInfo* _segment;

    // The connection the segment is attached to. Referenced until the image is released, since detaching needs it.
    private X11DisplayHandle? _attachedDisplay;

    /// <summary>
    /// Gets a value indicating whether the pixels are in a shared memory segment the server writes to (see <see cref="X11.XShmGetImage"/>).
    /// </summary>
    public bool IsShared => _segment != null;

    /// <summary>
    /// Gets the width of the image in pixels.
    /// </summary>
    public int Width => _image.width;

    /// <summary>
    /// Gets the height of the image in pixels.
    /// </summary>
    public int Height => _image.height;

    /// <summary>
    /// Gets the number of bytes per row.
    /// </summary>
    public int Stride => _image.bytes_per_line;

    /// <summary>
    /// Gets the number of bits per pixel.
    /// </summary>
    public int BitsPerPixel => _image.bits_per_pixel;

    /// <summary>
    /// Gets the pixel data of the image.
    /// </summary>
    public unsafe ReadOnlySpan<byte> Data => new(_image.data, _image.bytes_per_line * _image.height);

    /// <summary>
    /// Gets a value indicating whether the pixels are stored as <see cref="HPPH.ColorBGRA"/>.
    /// </summary>
    public bool IsBGRA => IsBGRAFormat(_image);

    /// <summary>
    /// Gets a description of the pixel format for error messages.
    /// </summary>
    public string Format => string.Create(CultureInfo.InvariantCulture,
                                          $"{_image.bits_per_pixel} bits per pixel, depth {_image.depth}, byte order {(_image.byte_order == X11.LSB_FIRST ? "LSBFirst" : "MSBFirst")}, masks R 0x{_image.red_mask.Value:X} G 0x{_image.green_mask.Value:X} B 0x{_image.blue_mask.Value:X}");

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="X11ImageHandle"/> class. Used by the interop marshaller.
    /// </summary>
    public X11ImageHandle()
        : base(true)
    { }

    #endregion

    #region Methods

    /// <summary>
    /// Creates an image of the screen's default visual in a System V shared memory segment that is attached to the server (MIT-SHM).
    /// </summary>
    /// <returns>The image with its description read; <c>null</c> if the server can't share memory with this process
    /// (no MIT-SHM or libXext, a remote server, or one in another IPC namespace).</returns>
    public static X11ImageHandle? CreateShared(X11DisplayHandle display, nint screen, int width, int height)
    {
        try
        {
            if (!X11.XShmQueryExtension(display)) return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }

        X11.XShmSegmentInfo* segment = (X11.XShmSegmentInfo*)NativeMemory.AllocZeroed((nuint)sizeof(X11.XShmSegmentInfo));
        X11ImageHandle image = X11.XShmCreateImage(display, X11.XDefaultVisualOfScreen(screen), (uint)X11.XDefaultDepthOfScreen(screen), X11.ZPIXMAP, 0, segment, (uint)width, (uint)height);
        if (image.IsInvalid)
        {
            image.Dispose();
            NativeMemory.Free(segment);
            return null;
        }

        // Released with the image from here on.
        image._segment = segment;

        try
        {
            image.ReadDescription();

            int id = X11.shmget(X11.IPC_PRIVATE, (nuint)((long)image.Stride * image.Height), X11.IPC_CREAT | X11.SHM_MODE_OWNER_READ_WRITE);
            if (id == -1) return Discard(image);

            nint address = X11.shmat(id, 0, 0);
            if (address == -1)
            {
                _ = X11.shmctl(id, X11.IPC_RMID, 0);
                return Discard(image);
            }

            segment->shmid = id;
            segment->shmaddr = (byte*)address;
            ((X11.XImage*)image.handle)->data = (byte*)address;

            display.ClearError();
            bool attached = X11.XShmAttach(display, segment);

            // Errors of the attach arrive with the sync.
            _ = X11.XSync(display, false);
            attached &= display.LastError == null;

            // Removed by the system once the last process detached it, also if this one crashes. The server has attached it by now, or never will.
            _ = X11.shmctl(id, X11.IPC_RMID, 0);

            if (!attached)
            {
                display.ClearError();
                return Discard(image);
            }

            bool referenced = false;
            display.DangerousAddRef(ref referenced);
            image._attachedDisplay = display;

            image.ReadDescription();
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }

        static X11ImageHandle? Discard(X11ImageHandle image)
        {
            image.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Reads the image description. Has to be called once after the image was created, before any other member is used.
    /// </summary>
    public void ReadDescription()
    {
        ObjectDisposedException.ThrowIf(IsInvalid || IsClosed, this);

        _image = *(X11.XImage*)handle;
    }

    /// <summary>
    /// Checks if an image stores its pixels as <see cref="HPPH.ColorBGRA"/>: 32 bits per pixel with the bytes B, G, R, X in memory.
    /// </summary>
    /// <remarks>The fourth byte is padding on a depth-24 visual.</remarks>
    internal static bool IsBGRAFormat(in X11.XImage image)
        => (image.bits_per_pixel == 32)
        && (image.byte_order == X11.LSB_FIRST)
        && (image.red_mask.Value == 0xFF0000)
        && (image.green_mask.Value == 0xFF00)
        && (image.blue_mask.Value == 0xFF);

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        if (_segment != null)
        {
            if (_attachedDisplay != null)
            {
                // Sent when the connection is flushed, at the latest when it's closed (possibly by this release).
                _ = X11.XShmDetach(_attachedDisplay.DangerousGetHandle(), _segment);
                _attachedDisplay.DangerousRelease();
            }

            if (_segment->shmaddr != null)
                _ = X11.shmdt((nint)_segment->shmaddr);

            // The pixels aren't Xlib's to free.
            ((X11.XImage*)handle)->data = null;
        }

        _ = X11.XDestroyImage(handle);

        if (_segment != null)
            NativeMemory.Free(_segment);

        return true;
    }

    #endregion
}