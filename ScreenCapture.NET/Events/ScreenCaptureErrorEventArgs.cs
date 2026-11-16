// ReSharper disable MemberCanBePrivate.Global

using System;
using System.Diagnostics.CodeAnalysis;

namespace ScreenCapture.NET;

/// <summary>
/// Represents the information supplied with an <see cref="IScreenCapture.ErrorOccurred" />-event.
/// </summary>
/// <param name="Exception">
/// The error. Errors of DirectX calls are <c>SharpGen.Runtime.SharpGenException</c>s carrying the HRESULT (<see cref="Exception.HResult"/>);
/// X11 errors are <see cref="InvalidOperationException"/>s whose message names the X error.
/// </param>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "It is the argument of the ErrorOccurred event. A struct can't derive from EventArgs, which EventHandler<T> doesn't require anymore.")]
public readonly record struct ScreenCaptureErrorEventArgs(Exception Exception);