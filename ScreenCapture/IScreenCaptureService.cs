using System;
using System.Collections.Generic;

namespace ScreenCapture
{
    public interface IScreenCaptureService : IDisposable
    {
        IEnumerable<GraphicsCard> GetGraphicsCards();
        IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard);
        IScreenCapture GetScreenCapture(Display display);
    }
}
