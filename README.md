# ScreenCapture.NET
[![GitHub release (latest by date)](https://img.shields.io/github/v/release/DarthAffe/ScreenCapture.NET?style=for-the-badge)](https://github.com/DarthAffe/ScreenCapture.NET/releases)
[![Nuget](https://img.shields.io/nuget/v/ScreenCapture.NET?style=for-the-badge)](https://www.nuget.org/packages/ScreenCapture.NET)
[![GitHub](https://img.shields.io/github/license/DarthAffe/ScreenCapture.NET?style=for-the-badge)](https://github.com/DarthAffe/ScreenCapture.NET/blob/master/LICENSE)
[![GitHub Repo stars](https://img.shields.io/github/stars/DarthAffe/ScreenCapture.NET?style=for-the-badge)](https://github.com/DarthAffe/ScreenCapture.NET/stargazers)

## NuGet-Packages:
| Package | Description |
|---------|-------------|
| [ScreenCapture.NET](https://www.nuget.org/packages/ScreenCapture.NET)| The core-package required to use ScreenCapture.NET captures or write your own. |
| [ScreenCapture.NET.WGC](https://www.nuget.org/packages/ScreenCapture.NET.WGC) | Windows.Graphics.Capture based capturing (Windows 10 1903 or newer). Fast and supports the whole set of features, works on hybrid systems and can capture single windows. **This should always be used if possible!** |
| [ScreenCapture.NET.DX11](https://www.nuget.org/packages/ScreenCapture.NET.DX11) | DirectX 11 based capturing. Fast, for systems where WGC isn't available. *Note: This might not work on windows hybrid systems (see [Troubleshooting](#troubleshooting)).* |
| [ScreenCapture.NET.DX9](https://www.nuget.org/packages/ScreenCapture.NET.DX9) | DirectX 9 based capturing. Slower than WGC and DX11 and does not support GPU-accelerated downscaling. Only useful if neither the WGC nor the DX11 package can be used for some reason. |
| [ScreenCapture.NET.X11](https://www.nuget.org/packages/ScreenCapture.NET.X11) | libX11 based capturing for the X-Window-System. Uses shared memory (MIT-SHM) if the X-Server supports it. On Wayland use the Wayland package. |
| [ScreenCapture.NET.Wayland](https://www.nuget.org/packages/ScreenCapture.NET.Wayland) | xdg-desktop-portal and PipeWire based capturing for Wayland desktops (KDE Plasma, GNOME, wlroots compositors with xdg-desktop-portal-wlr). The user has to allow the capture in a dialog of the desktop (see [Wayland](#wayland)). Needs libpipewire. |

## Usage
```csharp
// Create a screen-capture service
IScreenCaptureService screenCaptureService = new WGCScreenCaptureService();

// Get all available graphics cards
IEnumerable<GraphicsCard> graphicsCards = screenCaptureService.GetGraphicsCards();

// Get the displays from the graphics card(s) you are interested in
IEnumerable<Display> displays = screenCaptureService.GetDisplays(graphicsCards.First());

// Create a screen-capture for all screens you want to capture
IScreenCapture screenCapture = screenCaptureService.GetScreenCapture(displays.First());

// Register the regions you want to capture on the screen
// Capture the whole screen
ICaptureZone fullscreen = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, screenCapture.Display.Width, screenCapture.Display.Height));
// Capture a 100x100 region at the top left and scale it down to 50x50
ICaptureZone topLeft = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 100, 100), downscaleLevel: 1);

// Capture the screen
// This should be done in a loop on a separate thread as CaptureScreen blocks if the screen is not updated (still image) - see the ScreenCaptureLoop below.
CaptureResult result = screenCapture.CaptureScreen();

// Only NewFrame means that there is new data. With NoChange and Timeout the zones still contain the last frame.
// Everything else means that nothing can be captured right now (e.g. the display was disconnected) - screenCapture.LastError tells you why.
if (result == CaptureResult.NewFrame)
{
    // Acquire a frame to access the data. Remember to dispose it - the data of the frame doesn't change until you do.
    using CaptureFrame frame = fullscreen.AcquireFrame();

    // You have multiple options now:
    // 1. Access the raw byte-data
    ReadOnlySpan<byte> rawData = frame.RawBuffer;

    // 2. Use the provided abstraction to access pixels without having to care about low-level byte handling
    // All captures of this library use BGRA
    RefImage<ColorBGRA> image = frame.AsRefImage<ColorBGRA>();

    // Iterate all pixels of the image
    foreach (ColorBGRA color in image)
        Console.WriteLine($"A: {color.A}, R: {color.R}, G: {color.G}, B: {color.B}");

    // Get the pixel at location (x = 10, y = 20)
    ColorBGRA imageColorExample = image[10, 20];

    // Get the first row
    ImageRow<ColorBGRA> row = image.Rows[0];
    // Get the 10th pixel of the row
    ColorBGRA rowColorExample = row[10];

    // Get the first column
    ImageColumn<ColorBGRA> column = image.Columns[0];
    // Get the 10th pixel of the column
    ColorBGRA columnColorExample = column[10];

    // Cuts a rectangle out of the original image (x = 100, y = 150, width = 400, height = 300)
    RefImage<ColorBGRA> subImage = image[100, 150, 400, 300];

    // All of the things above (rows, columns, sub-images) do NOT allocate new memory so they are fast and memory efficient,
    // but they are only valid until the frame is disposed. If you need to keep the data, copy it.
    IImage copy = frame.CopyImage();
}

// Zones can be moved, resized or removed at any time
screenCapture.UpdateCaptureZone(topLeft, topLeft.Region with { X = 200 });
screenCapture.UnregisterCaptureZone(topLeft);
```

If you know which Capture-provider you're using it performs a bit better to not use the abstraction but a more low-level approach instead.   
This is the same example as above but without using the interfaces:
```csharp
WGCScreenCaptureService screenCaptureService = new WGCScreenCaptureService();
IEnumerable<GraphicsCard> graphicsCards = screenCaptureService.GetGraphicsCards();
IEnumerable<Display> displays = screenCaptureService.GetDisplays(graphicsCards.First());
WGCScreenCapture screenCapture = screenCaptureService.GetScreenCapture(displays.First());

CaptureZone<ColorBGRA> fullscreen = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, screenCapture.Display.Width, screenCapture.Display.Height));
CaptureZone<ColorBGRA> topLeft = screenCapture.RegisterCaptureZone(new CaptureRegion(0, 0, 100, 100), downscaleLevel: 1);

if (screenCapture.CaptureScreen() == CaptureResult.NewFrame)
{
    using CaptureFrame<ColorBGRA> frame = fullscreen.AcquireFrame();

    RefImage<ColorBGRA> image = frame.Image;

    // You can also access the pixels as a span directly
    // ReadOnlySpan<ColorBGRA> pixels = frame.Pixels;

    foreach (ColorBGRA color in image)
        Console.WriteLine($"A: {color.A}, R: {color.R}, G: {color.G}, B: {color.B}");

    ColorBGRA imageColorExample = image[10, 20];

    ImageRow<ColorBGRA> row = image.Rows[0];
    ColorBGRA rowColorExample = row[10];

    ImageColumn<ColorBGRA> column = image.Columns[0];
    ColorBGRA columnColorExample = column[10];

    RefImage<ColorBGRA> subImage = image[100, 150, 400, 300];

    Image<ColorBGRA> copy = frame.CopyImage();
}
```

The images are from [HPPH](https://github.com/DarthAffe/HPPH), check there for everything else you can do with them (average colors, color palettes, conversions, ...).

## Black bars
If you're capturing videos or games you might want to ignore black bars at the edges of the screen:
```csharp
using CaptureFrame<ColorBGRA> frame = fullscreen.AcquireFrame();

// Cuts off all black bars - everything darker than the threshold counts as black
RefImage<ColorBGRA> content = frame.Image.RemoveBlackBars(threshold: 8);

// Or only get the size of a single bar
int top = frame.Image.CalculateTop(threshold: 8);
```

## Comparing frames
Images are compared by reference with `Equals`. To check if the content changed between two captures, keep a copy of the frame and use `ContentEquals`:
```csharp
using CaptureFrame<ColorBGRA> frame = fullscreen.AcquireFrame();

Image<ColorBGRA> current = frame.CopyImage();
bool changed = (previous == null) || !current.ContentEquals(previous);
previous = current;
```

## Capturing continuously
The `ScreenCaptureLoop` captures on a background thread with the frame rate you give it. Each zone raises its `Updated`-event when it got new data:
```csharp
using ScreenCaptureLoop loop = new(screenCapture, targetFrameRate: 30);

topLeft.Updated += (_, _) =>
{
    using CaptureFrame<ColorBGRA> frame = topLeft.AcquireFrame();
    ColorBGRA average = frame.Image.Average();
};

loop.Start();
...
loop.Stop();
```

If you prefer async code there's also `screenCapture.CaptureLoopAsync(targetFrameRate, cancellationToken)`, which returns the result of every capture as an `IAsyncEnumerable<CaptureResult>`.

Everything can be used from multiple threads. The events are raised on the thread of the capture (the loop) and you can use the capture and its zones in there.
Frames should only be held for a short time and need to be disposed on the thread that acquired them. Holding a frame never blocks the capture, but if it's still held when the next but one capture runs, the zone skips that capture.

## Storing displays
The `Id` of a display stays the same across restarts (on Windows and X11), so you can store it in your settings and find the display again with `screenCaptureService.TryGetDisplay(id, out Display display)`.

## HDR
If you want to capture a display that has HDR enabled you should set `CaptureHdr`. The HDR image is then mapped to the colors the display would show without HDR.
```csharp
WGCScreenCaptureService screenCaptureService = new(new WGCCaptureOptions { CaptureHdr = true });
```
The DX11 package has the same option. There it's even more important (without it the capture is way too bright), but it needs a per-monitor DPI aware process,
so call `DPIAwareness.Initialize()` before any window is created (or set it in the application manifest).
```csharp
DPIAwareness.Initialize();
DX11ScreenCaptureService screenCaptureService = new(new DX11CaptureOptions { CaptureHdr = true });
```

## Capturing windows
The WGC package can capture single windows. They are captured even if they are covered by other windows.
```csharp
WGCScreenCaptureService screenCaptureService = new();
WGCScreenCapture screenCapture = screenCaptureService.GetWindowCapture(process.MainWindowHandle);
```

## X11
To survive a lost connection to the X-Server (e.g. if a remote server goes away) libX11 1.7 or newer is needed. With older versions Xlib terminates the process in that case.   
The library installs process-wide Xlib error handlers. Errors of other connections are passed on to the handlers that were installed before.

## Wayland
Wayland desktops only share the screen with the consent of the user. The first `GetDisplays` of a `WaylandScreenCaptureService` starts a screen cast session: the desktop shows a dialog where the user picks the monitors to share, and `GetDisplays` blocks until the user answered it (it throws an `InvalidOperationException` if the user cancels). The displays are the shared monitors.

The desktop can remember the selection: the service then has a restore token that starts a later session without the dialog. Store it and pass it back on the next start:
```csharp
WaylandScreenCaptureService screenCaptureService = new(sessionOptions: new WaylandSessionOptions { RestoreToken = LoadToken() });

// The desktop may hand out a new token with every session (GNOME does): always store the latest one.
screenCaptureService.RestoreTokenChanged += (_, args) => SaveToken(args.RestoreToken);

IEnumerable<Display> displays = screenCaptureService.GetDisplays(screenCaptureService.GetGraphicsCards().First());
```

If the user stops sharing (or the session ends otherwise), the captures return `CaptureResult.Unavailable`. They never show the dialog themselves: the next `GetDisplays` starts a new session (restored without the dialog if the desktop remembered it), and the captures continue on it. `WaylandSessionOptions.DrawCursor` draws the mouse pointer into the frames.

The ids of the displays are only valid as long as the session. On wlroots desktops (xdg-desktop-portal-wlr, e.g. sway) rotated monitors are captured sideways.

## Troubleshooting
**DX11 doesn't work on a hybrid system (laptop with two GPUs)**  
Desktop Duplication can only capture a display from the GPU it's connected to. Use the WGC package, or run your application on the GPU of the display (check [the docs](https://learn.microsoft.com/en-us/troubleshoot/windows-client/shell-experience/error-when-dda-capable-app-is-against-gpu) and [this issue](https://github.com/DarthAffe/ScreenCapture.NET/issues/39)).

**DX11 or DX9 captures are `Unavailable` for a while**  
While a UAC prompt or the lock screen is shown nothing can be captured. The capture continues on its own afterwards.

**X11 doesn't show the desktop**  
If you're on a Wayland desktop `$DISPLAY` is usually XWayland, which doesn't show the desktop to X11 clients. Use the Wayland package.
