// ReSharper disable MemberCanBePrivate.Global

using System;
using System.Diagnostics.CodeAnalysis;

namespace ScreenCapture.NET;

/// <summary>
/// Represents the information supplied with an <see cref="IScreenCapture.ErrorOccurred" />-event.
/// </summary>
/// <param name="Exception">
/// The error. DirectX errors are <c>SharpGen.Runtime.SharpGenException</c>s and WGC errors <see cref="System.Runtime.InteropServices.COMException"/>s, both with the HRESULT.
/// X11 and Wayland errors are mostly <see cref="InvalidOperationException"/>s naming the native error.
/// </param>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Event argument; a struct can't derive from EventArgs.")]
public readonly record struct ScreenCaptureErrorEventArgs(Exception Exception);