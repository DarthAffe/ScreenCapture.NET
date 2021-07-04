# ScreenCapture.NET
Vortice based Desktop Duplication

NuGet: https://www.nuget.org/packages/ScreenCapture.NET

## Usage
```csharp
// Sets the DPI-awareness of the application - this is required for capturing.
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
// This should be done in a loop on a seperate thread as CaptureScreen blocks if the screen is not updated (still image).
screenCapture.CaptureScreen();

// Do something with the captured image - e.g. access all pixels (same could be done with topLeft)
// Locking is not neccessary in that case as we're capturing in the same thread,
// but when using a threaded-approach (which is recommended) it prevents potential tearing of the data in the buffer.
lock (fullscreen.Buffer)
{
    // Stride is the width in bytes of a row in the buffer (width in pixel * bytes per pixel)
    int stride = fullscreen.Stride;

    Span<byte> data = new(fullscreen.Buffer);

    // Iterate all rows of the image
    for (int y = 0; y < fullscreen.Height; y++)
    {
        // Select the actual data of the row
        Span<byte> row = data.Slice(y * stride, stride);

        // Iterate all pixels
        for (int x = 0; x < row.Length; x += fullscreen.BytesPerPixel)
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