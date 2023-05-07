# ScreenCapture.NET
[![GitHub release (latest by date)](https://img.shields.io/github/v/release/DarthAffe/ScreenCapture.NET?style=for-the-badge)](https://github.com/DarthAffe/ScreenCapture.NET/releases)
[![Nuget](https://img.shields.io/nuget/v/ScreenCapture.NET?style=for-the-badge)](https://www.nuget.org/packages/ScreenCapture.NET)
[![GitHub](https://img.shields.io/github/license/DarthAffe/ScreenCapture.NET?style=for-the-badge)](https://github.com/DarthAffe/ScreenCapture.NET/blob/master/LICENSE)
[![GitHub Repo stars](https://img.shields.io/github/stars/DarthAffe/ScreenCapture.NET?style=for-the-badge)](https://github.com/DarthAffe/ScreenCapture.NET/stargazers)

## Usage
```csharp
// Create a screen-capture service
IScreenCaptureService screenCaptureService = new DX11ScreenCaptureService();

// Get all available graphics cards
IEnumerable<GraphicsCard> graphicsCards = screenCaptureService.GetGraphicsCards();

// Get the displays from the graphics card(s) you are interested in
IEnumerable<Display> displays = screenCaptureService.GetDisplays(graphicsCards.First());

// Create a screen-capture for all screens you want to capture
IScreenCapture screenCapture = screenCaptureService.GetScreenCapture(displays.First());

// Register the regions you want to capture on the screen
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

// Move the top left zone more towards the center
// Using the Update-method allows to move the zone without having to allocate 
// new buffers and textures which yields a good performance gain if done at high framerates.
screenCapture.UpdateCaptureZone(topLeft, x: 100, y: 200);

// Note that resizing the zone is also possible but currently reinitializes the zone
// -> no performance gain compared to removing and readding the zone.
screenCapture.UpdateCaptureZone(topLeft, width: 20, height: 20);
```
