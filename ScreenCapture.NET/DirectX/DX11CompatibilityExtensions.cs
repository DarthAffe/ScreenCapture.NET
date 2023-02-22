using Vortice.DXGI;

namespace ScreenCapture.NET;

// DarthAffe 22.02.2023: These helper-methods where removed from Vortice and are readded here since they are used.
internal static class DX11CompatibilityExtensions
{
    public static IDXGIAdapter1 GetAdapter1(this IDXGIFactory1 factory, int index)
    {
        factory.EnumAdapters1(index, out IDXGIAdapter1 adapter).CheckError();
        return adapter;
    }

    public static IDXGIOutput GetOutput(this IDXGIAdapter1 adapter, int index)
    {
        adapter.EnumOutputs(index, out IDXGIOutput output).CheckError();
        return output;
    }
}