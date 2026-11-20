using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// A private Xvfb server (virtual framebuffer X server) for X11 tests that need a server with known behavior, e.g. one that leaves the padding byte of its pixels 0.
/// The test is inconclusive if Xvfb isn't installed.
/// </summary>
internal sealed partial class XvfbServer : IDisposable
{
    #region Properties & Fields

    // The arguments of Xvfb without a display name, and whether it runs in its own IPC namespace.
    private readonly List<string> _arguments;
    private readonly bool _ownIpcNamespace;

    private Process _process;

    /// <summary>
    /// Gets the name of the X display (e.g. <c>:3</c>) to connect to.
    /// </summary>
    public string DisplayName { get; }

    #endregion

    #region Constructors

    private XvfbServer(List<string> arguments, bool ownIpcNamespace, Process process, string displayName)
    {
        this._arguments = arguments;
        this._ownIpcNamespace = ownIpcNamespace;
        this._process = process;
        this.DisplayName = displayName;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Starts a server with one screen and waits until it accepts connections.
    /// </summary>
    /// <param name="screen">The size and depth of the screen (<c>WxHxD</c>).</param>
    /// <param name="sharedMemoryExtension">Whether the server offers MIT-SHM.</param>
    /// <param name="ownIpcNamespace">Whether the server runs in its own IPC namespace (<c>unshare</c>): it offers MIT-SHM but can't attach this process's segments, like a remote server.</param>
    /// <param name="randrExtension">Whether the server offers RandR (its screen has one monitor named <c>screen</c>).</param>
    public static XvfbServer Start(string screen = "640x480x24", bool sharedMemoryExtension = true, bool ownIpcNamespace = false, bool randrExtension = true)
    {
        // -displayfd picks a free display number (both TFM test processes run at once) and writes it once the server is ready.
        // Without -noreset the server resets (e.g. its root background) whenever its last client disconnects.
        List<string> arguments = ["-displayfd", "1", "-screen", "0", screen, "-nolisten", "tcp", "-noreset"];
        if (!sharedMemoryExtension)
            arguments.AddRange(["-extension", "MIT-SHM"]);
        if (!randrExtension)
            arguments.AddRange(["-extension", "RANDR"]);

        (Process process, string displayNumber) = Launch(arguments, ownIpcNamespace);
        return new XvfbServer(arguments, ownIpcNamespace, process, $":{displayNumber}");
    }

    /// <summary>
    /// Kills the server, like a crash: its clients' connections break.
    /// </summary>
    public void Kill()
    {
        if (!_process.HasExited)
        {
            // unshare execs Xvfb, so this is the server in both cases.
            _process.Kill();
            _process.WaitForExit();
        }
    }

    /// <summary>
    /// Kills the server if it's still running and starts it again with the same display name and arguments (but none of its state, e.g. the root pattern).
    /// </summary>
    public void Restart()
    {
        Kill();
        _process.Dispose();

        // An explicit display number is used as is; -displayfd still tells when the server is ready.
        (_process, string displayNumber) = Launch([DisplayName, .. _arguments], _ownIpcNamespace);
        Assert.AreEqual(DisplayName, $":{displayNumber}", "The server restarted with another display.");
    }

    private static (Process process, string displayNumber) Launch(List<string> arguments, bool ownIpcNamespace)
    {
        // -r maps the user to root in a new user namespace, which allows the IPC namespace without privileges.
        ProcessStartInfo startInfo = ownIpcNamespace ? new ProcessStartInfo("unshare", ["-r", "--ipc", "Xvfb", .. arguments]) : new ProcessStartInfo("Xvfb", arguments);
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception)
        {
            Assert.Inconclusive($"{startInfo.FileName} isn't installed.");
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
            {
                // Unprivileged user namespaces can be turned off by the system.
                if (ownIpcNamespace)
                    Assert.Inconclusive($"Xvfb didn't start in its own IPC namespace: {errors}");

                Assert.Fail($"Xvfb didn't start: {errors}");
            }
        }

        return (process, displayNumber);
    }

    /// <summary>
    /// Runs <c>xrandr</c> on the server (the test is inconclusive if it isn't installed), e.g. <c>--setmonitor left 320/1x480/1+0+0 none</c> to add a monitor without output.
    /// Monitors can't be redefined: remove one with <c>--delmonitor</c> before setting it again.
    /// </summary>
    public void Xrandr(params string[] arguments) => Run("xrandr", ["-display", DisplayName, .. arguments]);

    private static void Run(string fileName, IEnumerable<string> arguments)
    {
        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = false, RedirectStandardError = true });
        }
        catch (Win32Exception)
        {
            Assert.Inconclusive($"{fileName} isn't installed.");
        }

        using (process)
        {
            Assert.IsNotNull(process, $"{fileName} didn't start.");
            string errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, $"{fileName} {string.Join(' ', arguments)} failed: {errors}");
        }
    }

    /// <summary>
    /// Tiles the root window with a random two-colored pattern (<c>xsetroot</c>, the test is inconclusive if it isn't installed).
    /// The tile is 61x37 pixels, so a capture from a wrong offset or with a wrong stride shows other pixels.
    /// </summary>
    public void SetRootPattern(int seed)
    {
        const int width = 61;
        const int height = 37;

        Random random = new(seed);
        byte[] bits = new byte[((width + 7) / 8) * height];
        random.NextBytes(bits);

        string path = Path.Combine(Path.GetTempPath(), $"ScreenCapture.NET.Tests-{Environment.ProcessId}-{seed}.xbm");
        File.WriteAllText(path, $"#define pattern_width {width}\n#define pattern_height {height}\nstatic unsigned char pattern_bits[] = {{\n{string.Join(",\n", bits.Select(b => $"0x{b:x2}"))}}};\n");

        try
        {
            Run("xsetroot", ["-display", DisplayName, "-bitmap", path, "-fg", Color(random), "-bg", Color(random)]);
        }
        finally
        {
            File.Delete(path);
        }

        static string Color(Random random) => string.Create(CultureInfo.InvariantCulture, $"#{random.Next(0x1000000):x6}");
    }

    /// <summary>
    /// Sets the EDID of the output of the server's automatic monitor <c>screen</c> to a base block with the given monitor name (display descriptor 0xFC, up to 13 characters).
    /// </summary>
    [SupportedOSPlatform("linux")]
    public unsafe void SetMonitorName(string monitorName)
    {
        const int xaInteger = 19;
        const int propModeReplace = 0;

        byte[] edid = new byte[128];
        ((ReadOnlySpan<byte>)[0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00]).CopyTo(edid);

        // The first descriptor is a detailed timing (pixel clock != 0), the name is in the second.
        edid[54] = 0x01;
        edid[72 + 3] = 0xFC;
        // Ended by a line feed if shorter than 13 characters, then padded with spaces.
        Span<byte> text = edid.AsSpan(72 + 5, 13);
        text.Fill((byte)' ');
        int length = System.Text.Encoding.ASCII.GetBytes(monitorName, text);
        if (length < text.Length)
            text[length] = (byte)'\n';

        using X11DisplayHandle display = X11DisplayHandle.Open(DisplayName);
        Assert.IsFalse(display.IsInvalid, "Couldn't open the display.");

        CULong root = X11.XRootWindowOfScreen(X11.XScreenOfDisplay(display, 0));
        X11.XRRMonitorInfo* monitors = X11.XRRGetMonitors(display, root, getActive: true, out int count);
        try
        {
            CULong output = default;
            for (int i = 0; i < count; i++)
                if (monitors[i].noutput > 0)
                    output = monitors[i].outputs[0];

            Assert.AreNotEqual(0u, (uint)output.Value, "The server has no monitor with an output.");

            fixed (byte* data = edid)
                XRRChangeOutputProperty(display, output, X11.XInternAtom(display, "EDID", onlyIfExists: false), new CULong(xaInteger), 8, propModeReplace, data, edid.Length);

            // Sent and checked before the connection is closed.
            display.ClearError();
            _ = X11.XSync(display, false);
            Assert.IsNull(display.LastError, "Setting the EDID failed.");
        }
        finally
        {
            if (monitors != null)
                X11.XRRFreeMonitors(monitors);
        }
    }

    [LibraryImport("libXrandr.so.2")]
    private static unsafe partial void XRRChangeOutputProperty(X11DisplayHandle display, CULong output, CULong property, CULong type, int format, int mode, byte* data, int itemCount);

    /// <inheritdoc />
    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }

    #endregion
}
