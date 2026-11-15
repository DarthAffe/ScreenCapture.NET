using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// Calls the error handler like Xlib does, with made-up display addresses, so no X server is needed.
/// </summary>
[TestClass]
[SupportedOSPlatform("linux")] // The X11 assembly is marked linux-only, but the handler is managed code that also runs on Windows.
public sealed unsafe class X11ErrorHandlerTests
{
    private static int _forwardedCalls;
    private static nint _forwardedDisplay;

    [TestMethod]
    public void ErrorOfOwnDisplayIsRecorded()
    {
        nint display = 0x1001;
        X11ErrorHandler.DisplayErrors errors = X11ErrorHandler.Track(display);
        try
        {
            Assert.AreEqual(0, RaiseError(display, errorCode: 8, requestCode: 73, minorCode: 2));
            Assert.AreEqual(new X11Error(8, 73, 2), errors.LastError);

            errors.Clear();
            Assert.IsNull(errors.LastError);
        }
        finally
        {
            X11ErrorHandler.Unregister(display, errors);
        }
    }

    [TestMethod]
    public void ErrorOfOtherDisplayIsPassedOn()
    {
        nint display = 0x1002;
        nint previousHandler = X11ErrorHandler.PreviousHandler;
        X11ErrorHandler.PreviousHandler = (nint)(delegate* unmanaged[Cdecl]<nint, X11.XErrorEvent*, int>)&CountingHandler;
        try
        {
            _forwardedCalls = 0;

            Assert.AreEqual(42, RaiseError(display, errorCode: 3, requestCode: 1, minorCode: 0));
            Assert.AreEqual(1, _forwardedCalls);
            Assert.AreEqual(display, _forwardedDisplay);
        }
        finally
        {
            X11ErrorHandler.PreviousHandler = previousHandler;
        }
    }

    [TestMethod]
    public void ErrorOfUnregisteredDisplayIsNotRecorded()
    {
        nint display = 0x1003;
        X11ErrorHandler.DisplayErrors errors = X11ErrorHandler.Track(display);
        X11ErrorHandler.Unregister(display, errors);

        nint previousHandler = X11ErrorHandler.PreviousHandler;
        X11ErrorHandler.PreviousHandler = 0;
        try
        {
            RaiseError(display, errorCode: 8, requestCode: 73, minorCode: 0);
            Assert.IsNull(errors.LastError);
        }
        finally
        {
            X11ErrorHandler.PreviousHandler = previousHandler;
        }
    }

    /// <summary>
    /// A closed display's address can be reused by a new display before the old one is unregistered.
    /// </summary>
    [TestMethod]
    public void UnregisterKeepsNewerRegistrationOfSameAddress()
    {
        nint display = 0x1004;
        X11ErrorHandler.DisplayErrors closed = X11ErrorHandler.Track(display);
        X11ErrorHandler.DisplayErrors reopened = X11ErrorHandler.Track(display);
        try
        {
            X11ErrorHandler.Unregister(display, closed);

            RaiseError(display, errorCode: 8, requestCode: 73, minorCode: 0);
            Assert.AreEqual(new X11Error(8, 73, 0), reopened.LastError);
            Assert.IsNull(closed.LastError);
        }
        finally
        {
            X11ErrorHandler.Unregister(display, reopened);
        }
    }

    /// <summary>
    /// Pins the layout of <see cref="X11.XErrorEvent"/> to Xlib's on 64-bit Linux.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public void XErrorEventMatchesXlibLayoutOn64BitLinux()
    {
        if (!Environment.Is64BitProcess)
            Assert.Inconclusive("The layout is pinned for 64-bit processes.");

        Assert.AreEqual(8, (int)Marshal.OffsetOf<X11.XErrorEvent>(nameof(X11.XErrorEvent.display)));
        Assert.AreEqual(16, (int)Marshal.OffsetOf<X11.XErrorEvent>(nameof(X11.XErrorEvent.resourceid)));
        Assert.AreEqual(24, (int)Marshal.OffsetOf<X11.XErrorEvent>(nameof(X11.XErrorEvent.serial)));
        Assert.AreEqual(32, (int)Marshal.OffsetOf<X11.XErrorEvent>(nameof(X11.XErrorEvent.error_code)));
        Assert.AreEqual(33, (int)Marshal.OffsetOf<X11.XErrorEvent>(nameof(X11.XErrorEvent.request_code)));
        Assert.AreEqual(34, (int)Marshal.OffsetOf<X11.XErrorEvent>(nameof(X11.XErrorEvent.minor_code)));
    }

    private static int RaiseError(nint display, byte errorCode, byte requestCode, byte minorCode)
    {
        X11.XErrorEvent error = new()
        {
            type = 0,
            display = display,
            error_code = errorCode,
            request_code = requestCode,
            minor_code = minorCode
        };

        return X11ErrorHandler.Handler(display, &error);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CountingHandler(nint display, X11.XErrorEvent* error)
    {
        _forwardedCalls++;
        _forwardedDisplay = display;
        return 42;
    }
}
