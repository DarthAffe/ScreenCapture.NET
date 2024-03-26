# ScreenCapture.NET
[![GitHub release (latest by date)](https://img.shields.io/github/v/release/DarthAffe/ScreenCapture.NET?style=for-the-badge)](https://github.com/DarthAffe/ScreenCapture.NET/releases)
[![Nuget](https://img.shields.io/nuget/v/ScreenCapture.NET?style=for-the-badge)](https://www.nuget.org/packages/ScreenCapture.NET)
[![GitHub](https://img.shields.io/github/license/DarthAffe/ScreenCapture.NET?style=for-the-badge)](https://github.com/DarthAffe/ScreenCapture.NET/blob/master/LICENSE)
[![GitHub Repo stars](https://img.shields.io/github/stars/DarthAffe/ScreenCapture.NET?style=for-the-badge)](https://github.com/DarthAffe/ScreenCapture.NET/stargazers)

## NuGet-Packages:
| Package | Description |
|---------|-------------|
| [ScreenCapture.NET](https://www.nuget.org/packages/ScreenCapture.NET)| The core-package required to use ScreenCapture.NET captures or write your own. |
| [ScreenCapture.NET.DX11](https://www.nuget.org/packages/ScreenCapture.NET.DX11) | DirectX 11 based capturing. Fast and supports the whole set of features. **This should always be used if possible!** |
| [ScreenCapture.NET.DX9](https://www.nuget.org/packages/ScreenCapture.NET.DX9) | DirectX 9 based  capturing. Slower then DX 11 and does not support rotated screens and GPU-accelerated downscaling. Only useful if the DX11 package can't be used for some reason. |
| [ScreenCapture.NET.X11](https://www.nuget.org/packages/ScreenCapture.NET.X11) | libX11 based capturing for the X-Window-System. Currently the only way to use ScreenCapture.NET on linux. Quite slow and can easily break depending on the X-Server config. Works on my machine, but it's not really a high proprity to support at the moment. Does not support rotated screens and GPU-accelerated downscaling. |

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
ICaptureZone fullscreen = screenCapture.RegisterCaptureZone(0, 0, screenCapture.Display.Width, screenCapture.Display.Height);
// Capture a 100x100 region at the top left and scale it down to 50x50
ICaptureZone topLeft = screenCapture.RegisterCaptureZone(0, 0, 100, 100, downscaleLevel: 1);

// Capture the screen
// This should be done in a loop on a separate thread as CaptureScreen blocks if the screen is not updated (still image).
screenCapture.CaptureScreen();

// Do something with the captured image - e.g. access all pixels (same could be done with topLeft)

//Lock the zone to access the data. Remember to dispose the returned disposable to unlock again.
using (fullscreen.Lock())
{
    // You have multiple options now:
    // 1. Access the raw byte-data
    ReadOnlySpan<byte> rawData = fullscreen.RawBuffer;

    // 2. Use the provided abstraction to access pixels without having to care about low-level byte handling
    // Get the image captured for the zone
    IImage image = fullscreen.Image;

    // Iterate all pixels of the image
    foreach (IColor color in image)
        Console.WriteLine($"A: {color.A}, R: {color.R}, G: {color.G}, B: {color.B}");

    // Get the pixel at location (x = 10, y = 20)
    IColor imageColorExample = image[10, 20];

    // Get the first row
    IImage.IImageRow row = image.Rows[0];
    // Get the 10th pixel of the row
    IColor rowColorExample = row[10];

    // Get the first column
    IImage.IImageColumn column = image.Columns[0];
    // Get the 10th pixel of the column
    IColor columnColorExample = column[10];

    // Cuts a rectangle out of the original image (x = 100, y = 150, width = 400, height = 300)
    IImage subImage = image[100, 150, 400, 300];

    // All of the things above (rows, columns, sub-images) do NOT allocate new memory so they are fast and memory efficient, but for that reason don't provide raw byte access.
}
```

IF you know which Capture-provider you're using it performs a bit better to not use the abstraction but a more low-level approach instead.   
This is the same example as above but without using the interfaces:
```csharp
DX11ScreenCaptureService screenCaptureService = new DX11ScreenCaptureService();
IEnumerable<GraphicsCard> graphicsCards = screenCaptureService.GetGraphicsCards();
IEnumerable<Display> displays = screenCaptureService.GetDisplays(graphicsCards.First());
DX11ScreenCapture screenCapture = screenCaptureService.GetScreenCapture(displays.First());

CaptureZone<ColorBGRA> fullscreen = screenCapture.RegisterCaptureZone(0, 0, screenCapture.Display.Width, screenCapture.Display.Height);
CaptureZone<ColorBGRA> topLeft = screenCapture.RegisterCaptureZone(0, 0, 100, 100, downscaleLevel: 1);

screenCapture.CaptureScreen();

using (fullscreen.Lock())
{
    RefImage<ColorBGRA> image = fullscreen.Image;

    foreach (ColorBGRA color in image)
        Console.WriteLine($"A: {color.A}, R: {color.R}, G: {color.G}, B: {color.B}");

    ColorBGRA imageColorExample = image[10, 20];

    ReadOnlyRefEnumerable<ColorBGRA> row = image.Rows[0];
    ColorBGRA rowColorExample = row[10];

    ReadOnlyRefEnumerable<ColorBGRA> column = image.Columns[0];
    ColorBGRA columnColorExample = column[10];

    RefImage<ColorBGRA> subImage = image[100, 150, 400, 300];
}
```
