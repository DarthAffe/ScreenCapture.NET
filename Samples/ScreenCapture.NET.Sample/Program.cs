using System.Globalization;
using HPPH;
using ScreenCapture.NET;

// This sample captures a display and prints the average color of the screen and the frame rate once per second.
// Pass the index of a display as argument to capture another one than the first (e.g. 'dotnet run -- 1'). Stop it with Ctrl+C.

// Use the first capture service that works on this system
using IScreenCaptureService screenCaptureService = CreateScreenCaptureService();
Console.WriteLine($"Capturing with {screenCaptureService.GetType().Name}");

// Get the displays of all graphics cards
// On Wayland this shows the dialog of the desktop where you pick the monitors to share
List<Display> displays = [.. screenCaptureService.GetGraphicsCards().SelectMany(screenCaptureService.GetDisplays)];
for (int i = 0; i < displays.Count; i++)
    Console.WriteLine($"  [{i}] {Describe(displays[i])}");

int index = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 0;
if ((index < 0) || (index >= displays.Count))
{
    Console.WriteLine($"There's no display {index}.");
    return;
}

Display display = displays[index];
using IScreenCapture screenCapture = screenCaptureService.GetScreenCapture(display);

// Capture the whole display, scaled down to an eighth of its size (downscale level 3)
// That's plenty for an average color and a lot cheaper than the full size
ICaptureZone zone = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, display.Width, display.Height), downscaleLevel: 3);

// If the display changes (e.g. its resolution) the zone has to be fitted to it - a zone that doesn't lie within the display isn't captured anymore
screenCapture.DisplayChanged += (_, e) =>
{
    Console.WriteLine($"Display changed: {Describe(e.Display)}");
    screenCapture.UpdateCaptureZone(zone, new CaptureRegion(0, 0, e.Display.Width, e.Display.Height));
};

// All events are raised on the thread of the capture loop, so everything the status line shows is guarded by this lock
Lock statsLock = new();
Dictionary<CaptureResult, int> results = [];
ColorBGRA average = default;

// Raised at the end of every capture, whatever the result
screenCapture.Updated += (_, e) =>
{
    lock (statsLock)
        results[e.Result] = results.GetValueOrDefault(e.Result) + 1;
};

// Raised for every error - the capture recovers on its own if it can (see the results), so the errors are only printed here
screenCapture.ErrorOccurred += (_, e) => Console.WriteLine($"Error: {e.Exception.Message}");

// Raised when the zone got new data
zone.Updated += (_, _) =>
{
    // Only hold frames for a short time: compute (or copy) what you need and dispose them
    ColorBGRA color;
    using (CaptureFrame frame = zone.AcquireFrame())
        color = frame.AsRefImage<ColorBGRA>().Average();

    lock (statsLock)
        average = color;
};

// Capture on a background thread, 30 times per second
using ScreenCaptureLoop loop = new(screenCapture, targetFrameRate: 30);
loop.Start();

using CancellationTokenSource stop = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

Console.WriteLine($"Capturing {Describe(display)} - press Ctrl+C to stop");

// Print the status once per second until Ctrl+C is pressed (or the loop ended)
using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
try
{
    while (loop.IsRunning && await timer.WaitForNextTickAsync(stop.Token))
    {
        lock (statsLock)
        {
            // Captures without a new frame are listed by their result (e.g. Timeout on a still screen, Unavailable while the lock screen is shown)
            string others = string.Join(", ", results.Where(r => r.Key != CaptureResult.NewFrame).Select(r => $"{r.Key}: {r.Value}"));
            Console.WriteLine($"{results.GetValueOrDefault(CaptureResult.NewFrame),3} frames/s | average color #{average.R:X2}{average.G:X2}{average.B:X2}{(others.Length > 0 ? $" | {others}" : "")}");
            results.Clear();
        }
    }
}
catch (OperationCanceledException) { }

loop.Stop();

// The loop only ends by itself if a capture threw an exception (which it isn't supposed to do) - this rethrows it
await loop.Completion;

static IScreenCaptureService CreateScreenCaptureService()
{
    if (WGCScreenCaptureService.IsSupported())
        return new WGCScreenCaptureService();
    if (DX11ScreenCaptureService.IsSupported())
        return new DX11ScreenCaptureService();
    if (DX9ScreenCaptureService.IsSupported())
        return new DX9ScreenCaptureService();
    if (X11ScreenCaptureService.IsSupported()) // false on XWayland, as it doesn't show the Wayland desktop
        return new X11ScreenCaptureService();
    if (WaylandScreenCaptureService.IsSupported())
        return new WaylandScreenCaptureService();

    throw new PlatformNotSupportedException("Screen capturing isn't supported on this system.");
}

static string Describe(Display display)
{
    string name = string.IsNullOrEmpty(display.FriendlyName) ? display.DeviceName : display.FriendlyName;
    return $"{name} ({display.Width}x{display.Height} at {display.X},{display.Y}, rotation {display.Rotation}, id {display.Id})";
}
