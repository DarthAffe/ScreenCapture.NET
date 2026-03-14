# macOS Screen Capture (ScreenCaptureKit)

This module provides **macOS screen capturing support** for `ScreenCapture.NET` using Apple's **ScreenCaptureKit** framework.
It is intended to be used as a platform-specific backend similar to the existing **DX11** and **GDI** implementations.

ScreenCaptureKit offers **high-performance GPU-based screen capture** on macOS and is available starting from **macOS 12.3+**.

---

## Requirements

- macOS **12.3 or newer**
- .NET **6.0+** (or the same version used by the main solution)
- Apple **ScreenCaptureKit** framework available on the system

---

## Integration

The recommended approach is to **select the capture backend at compile time** depending on the target platform.

Example:

```csharp
#if MACOS
_screenCaptureService = new SCKScreenCaptureService();
#else
_screenCaptureService = new DX11ScreenCaptureService();
#endif
