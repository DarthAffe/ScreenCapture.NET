// ReSharper disable MemberCanBePrivate.Global

using System;
using System.Diagnostics.CodeAnalysis;

namespace ScreenCapture.NET;

/// <summary>
/// Represents the information supplied with an <see cref="IScreenCapture.ErrorOccurred" />-event.
/// </summary>
/// <param name="Exception">
/// The error. Errors of DirectX calls are <c>SharpGen.Runtime.SharpGenException</c>s carrying the HRESULT (<see cref="Exception.HResult"/>),
/// errors of Windows.Graphics.Capture calls (WGC) are <see cref="System.Runtime.InteropServices.COMException"/>s carrying it.
/// X11 errors are <see cref="InvalidOperationException"/>s whose message names the X error; Wayland errors are mostly <see cref="InvalidOperationException"/>s
/// whose message names the error of PipeWire or the portal.
/// </param>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "It is the argument of the ErrorOccurred event. A struct can't derive from EventArgs, which EventHandler<T> doesn't require anymore.")]
public readonly record struct ScreenCaptureErrorEventArgs(Exception Exception);