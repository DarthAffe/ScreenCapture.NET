using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ScreenCapture.NET;

/// <summary>
/// Owns an <c>XImage*</c> (including its pixel data) created with <see cref="X11.XGetImage"/>.
/// </summary>
/// <remarks>
/// If <see cref="X11.XGetImage"/> fails, the handle is invalid (<see cref="SafeHandle.IsInvalid"/>) and must not be used.
/// Readers of <see cref="Data"/> have to hold a reference (<see cref="SafeHandle.DangerousAddRef"/>) so the memory isn't freed meanwhile.
/// </remarks>
internal sealed class X11ImageHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    #region Properties & Fields

    private X11.XImage _image;

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
    /// Reads the image description. Has to be called once after the image was created, before any other member is used.
    /// </summary>
    public void ReadDescription()
    {
        ObjectDisposedException.ThrowIf(IsInvalid || IsClosed, this);

        _image = Marshal.PtrToStructure<X11.XImage>(handle);
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        X11.XDestroyImage(handle);
        return true;
    }

    #endregion
}