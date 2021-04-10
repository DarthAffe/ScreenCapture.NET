using System;

namespace ScreenCapture
{
    public interface IScreenCapture : IDisposable
    {
        Display Display { get; }

        bool CaptureScreen();
        CaptureZone RegisterCaptureZone(int x, int y, int width, int height, int downscaleLevel = 0);
        bool UnregisterCaptureZone(CaptureZone captureZone);
        void Restart();
    }
}
