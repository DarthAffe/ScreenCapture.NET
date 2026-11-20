using System.ComponentModel;
using System.Diagnostics;
using Tmds.DBus.Protocol;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// A private D-Bus session bus (<c>dbus-daemon</c>) for tests with a fake portal, so they never reach the real portal (which would show its dialog).
/// It activates no services, so a missing fake is a missing portal.
/// </summary>
/// <remarks>
/// Tmds.DBus keeps the session bus address it read first, so the bus is shared by the whole test process and has to be set up before anything reads the address:
/// tests that use the real session bus (the <c>Interactive</c> ones) can't run in the same process. The tests are inconclusive if they do or if dbus-daemon isn't installed.
/// The bus is stopped when the process exits.
/// </remarks>
internal static class PrivateSessionBus
{
    #region Properties & Fields

    private static readonly Lazy<string> LazyAddress = new(Start);

    /// <summary>
    /// Gets the address of the bus; it's also the session bus of the process (<c>DBUS_SESSION_BUS_ADDRESS</c>).
    /// </summary>
    public static string Address => LazyAddress.Value;

    #endregion

    #region Methods

    private static string Start()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("screencapture-net-dbus");
        string configFile = Path.Combine(directory.FullName, "bus.conf");
        File.WriteAllText(configFile, $"""
                                       <!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-Bus Bus Configuration 1.0//EN" "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
                                       <busconfig>
                                         <type>session</type>
                                         <listen>unix:tmpdir={directory.FullName}</listen>
                                         <policy context="default">
                                           <allow send_destination="*" eavesdrop="true"/>
                                           <allow eavesdrop="true"/>
                                           <allow own="*"/>
                                         </policy>
                                       </busconfig>
                                       """);

        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo("dbus-daemon", [$"--config-file={configFile}", "--nofork", "--print-address=1"])
            {
                UseShellExecute = false,
                RedirectStandardOutput = true
            });
        }
        catch (Win32Exception)
        {
            directory.Delete(true);
            Assert.Inconclusive("dbus-daemon isn't installed.");
        }

        Assert.IsNotNull(process, "dbus-daemon didn't start.");

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            process.Kill();
            process.WaitForExit();
            process.Dispose();
            directory.Delete(true);
        };

        string? address = process.StandardOutput.ReadLine();
        Assert.IsFalse(string.IsNullOrEmpty(address), "dbus-daemon didn't print its address.");

        Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", address);
        if (!string.Equals(DBusAddress.Session, address, StringComparison.Ordinal))
            Assert.Inconclusive("The session bus address was read before the private bus started: run these tests without the Interactive ones.");

        return address;
    }

    #endregion
}
