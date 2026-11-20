using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
    /// <param name="sharedMemoryExtension">Whether the server offers MIT-SHM.</param>
    /// <param name="ownIpcNamespace">Whether the server runs in its own IPC namespace (<c>unshare</c>): it offers MIT-SHM but can't attach this process's segments, like a remote server.</param>
    public static XvfbServer Start(string screen = "640x480x24", bool sharedMemoryExtension = true, bool ownIpcNamespace = false)
    {
        // -displayfd picks a free display number (both TFM test processes run at once) and writes it once the server is ready.
        // Without -noreset the server resets (e.g. its root background) whenever its last client disconnects.
        List<string> arguments = ["-displayfd", "1", "-screen", "0", screen, "-nolisten", "tcp", "-noreset"];
        if (!sharedMemoryExtension)
            arguments.AddRange(["-extension", "MIT-SHM"]);

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

        return new XvfbServer(process, $":{displayNumber}");
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
            Process? xsetroot = null;
            try
            {
                xsetroot = Process.Start(new ProcessStartInfo("xsetroot", ["-display", DisplayName, "-bitmap", path, "-fg", Color(random), "-bg", Color(random)]) { UseShellExecute = false });
            }
            catch (Win32Exception)
            {
                Assert.Inconclusive("xsetroot isn't installed.");
            }

            using (xsetroot)
            {
                Assert.IsNotNull(xsetroot, "xsetroot didn't start.");
                xsetroot.WaitForExit();
                Assert.AreEqual(0, xsetroot.ExitCode, "xsetroot failed.");
            }
        }
        finally
        {
            File.Delete(path);
        }

        static string Color(Random random) => string.Create(CultureInfo.InvariantCulture, $"#{random.Next(0x1000000):x6}");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_process.HasExited)
        {
            // unshare execs Xvfb, so this is the server in both cases.
            _process.Kill();
            _process.WaitForExit();
        }

        _process.Dispose();
    }

    #endregion
}
