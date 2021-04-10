using System.Collections.Generic;
using Vortice.DXGI;

namespace ScreenCapture
{
    public class DX11ScreenCaptureService : IScreenCaptureService
    {
        #region Properties & Fields

        private readonly IDXGIFactory1 _factory;

        private readonly Dictionary<Display, DX11ScreenCapture> _screenCaptures = new();

        #endregion

        #region Constructors

        public DX11ScreenCaptureService()
        {
            DXGI.CreateDXGIFactory1(out _factory).CheckError();
        }

        #endregion

        #region Methods

        public IEnumerable<GraphicsCard> GetGraphicsCards()
        {
            int i = 0;
            while (_factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success)
            {
                yield return new GraphicsCard(i, adapter.Description1.Description, adapter.Description1.VendorId, adapter.Description1.DeviceId);
                adapter.Dispose();
                i++;
            }
        }

        public IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard)
        {
            using IDXGIAdapter1? adapter = _factory.GetAdapter1(graphicsCard.Index);

            int i = 0;
            while (adapter.EnumOutputs(i, out IDXGIOutput output).Success)
            {
                int width = output.Description.DesktopCoordinates.Right - output.Description.DesktopCoordinates.Left;
                int height = output.Description.DesktopCoordinates.Bottom - output.Description.DesktopCoordinates.Top;
                yield return new Display(i, output.Description.DeviceName, width, height, graphicsCard);
                output.Dispose();
                i++;
            }
        }

        public IScreenCapture GetScreenCapture(Display display)
        {
            if (!_screenCaptures.TryGetValue(display, out DX11ScreenCapture? screenCapture))
                _screenCaptures.Add(display, screenCapture = new DX11ScreenCapture(_factory, display));
            return screenCapture;
        }

        public void Dispose()
        {
            foreach (DX11ScreenCapture screenCapture in _screenCaptures.Values)
                screenCapture.Dispose();
            _screenCaptures.Clear();

            _factory.Dispose();
        }

        #endregion
    }
}
