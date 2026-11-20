using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// A private Xvfb server (virtual framebuffer X server) for X11 tests that need a server with known behavior, e.g. one that leaves the padding byte of its pixels 0.
/// The test is inconclusive if Xvfb isn't installed.
/// </summary>
internal sealed class XvfbServer : IDisposable
{
    #region Properties & Fields

    private readonly Process _process;

    /// <summary>
    /// Gets the name of the X display (e.g. <c>:3</c>) to connect to.
    /// </summary>
    public string DisplayName { get; }

    #endregion

    #region Constructors

    private XvfbServer(Process process, string displayName)
    {
        this._process = process;
        this.DisplayName = displayName;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Starts a server with one screen and waits until it accepts connections.
    /// </summary>
    /// <param name="screen">The size and depth of the screen (<c>WxHxD</c>).</param>
    public static XvfbServer Start(string screen = "640x480x24")
    {
        // -displayfd picks a free display number (both TFM test processes run at once) and writes it once the server is ready.
        ProcessStartInfo startInfo = new("Xvfb", ["-displayfd", "1", "-screen", "0", screen, "-nolisten", "tcp"])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception)
        {
            Assert.Inconclusive("Xvfb isn't installed.");
        }

        Assert.IsNotNull(process, "Xvfb didn't start.");

        // Drained so the server doesn't block on a full pipe; only shown if it fails to start.
        StringBuilder errors = new();
        process.ErrorDataReceived += (_, e) => { lock (errors) errors.AppendLine(e.Data); };
        process.BeginErrorReadLine();

        string? displayNumber = process.StandardOutput.ReadLine();
        if (string.IsNullOrEmpty(displayNumber))
        {
            process.Kill();
            process.WaitForExit();
            lock (errors)
                Assert.Fail($"Xvfb didn't start: {errors}");
        }

        return new XvfbServer(process, $":{displayNumber}");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill();
            _process.WaitForExit();
        }

        _process.Dispose();
    }

    #endregion
}
