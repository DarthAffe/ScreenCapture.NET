# ScreenCapture.NET
Vortice based Desktop Duplication

NuGet: https://www.nuget.org/packages/ScreenCapture.NET

## Usage
```csharp
// Sets the DPI-awareness of the application - this is required for capturing
DPIAwareness.Initalize();

// Create a screen-capture service
IScreenCaptureService screenCaptureService = new DX11ScreenCaptureService();

// Get all available graphics cards
IEnumerable<GraphicsCard> graphicsCards = screenCaptureService.GetGraphicsCards();

// Get the displays from the graphics card(s) you are interested in
IEnumerable<Display> displays = screenCaptureService.GetDisplays(graphicsCards.First());

// Create a screen-capture for all screens you want to capture
IScreenCapture screenCapture = screenCaptureService.GetScreenCapture(displays.First());

// Register the regions you want to capture om the screen
// Capture the whole screen
CaptureZone fullscreen = screenCapture.RegisterCaptureZone(0, 0, screenCapture.Display.Width, screenCapture.Display.Height);
// Capture a 100x100 region at the top left and scale it down to 50x50
CaptureZone topLeft = screenCapture.RegisterCaptureZone(0, 0, 100, 100, downscaleLevel: 1);

// Capture the screen
// This should be done in a loop on a seperate thread as CaptureScreen blocks if the screen is not updated (still image)
screenCapture.CaptureScreen();

// Do something with the captured image - e.g. access all pixels (same could be done with topLeft)
// Locking is not neccessary in that case as we're capturing in the same thread,
// but when using a threaded-approach (which is recommended) it prevents potential tearing of the data in the buffer
lock (fullscreen.Buffer)
{
    // Since the size of the capture can be bigger than the size of our captured region due to size constraints on the GPU,
    // we need to use this for the stride. 
    // The 4 is the amount of bytes per pixel which is always 4 for the DX11ScreenCapture
    int stride = fullscreen.CaptureWidth * 4;

    Span<byte> data = new(fullscreen.Buffer);

    // Iterate all rows of the image
    for (int y = 0; y < fullscreen.Height; y++)
    {
        // Select the actual data of the row
        Span<byte> row = data.Slice(y * stride, fullscreen.Width * 4);

        // Iterate all pixels
        for (int x = 0; x < fullscreen.Width; x += 4)
        {
            // Data is in BGRA format for the DX11ScreenCapture
            byte b = row[x];
            byte g = row[x + 1];
            byte r = row[x + 2];
            byte a = row[x + 3];
        }
    }
}
```