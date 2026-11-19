using System;
using System.Collections.Generic;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// The capture a <see cref="D3D11ZonePipeline"/> writes the zones of: gives it the protected members of <see cref="AbstractScreenCapture{TColor}"/> it needs.
/// </summary>
/// <remarks>
/// Implemented explicitly by the capture, each member forwards to the member of the same name.
/// </remarks>
internal interface ID3D11ZonePipelineHost
{
    /// <inheritdoc cref="AbstractScreenCapture{TColor}.CaptureZones"/>
    IReadOnlyList<CaptureZone<ColorBGRA>> CaptureZones { get; }

    /// <inheritdoc cref="AbstractScreenCapture{TColor}.CalculateScaledSize"/>
    (int width, int height, int downscaleLevel) CalculateScaledSize(int width, int height, int downscaleLevel);

    /// <inheritdoc cref="AbstractScreenCapture{TColor}.GetSourceRegion(CaptureRegion, Display)"/>
    CaptureRegion GetSourceRegion(CaptureRegion region, Display display);

    /// <inheritdoc cref="AbstractScreenCapture{TColor}.WriteCaptureZoneFromScaled"/>
    void WriteCaptureZoneFromScaled(RefImage<ColorBGRA> scaled, CaptureZone<ColorBGRA> captureZone, Span<ColorBGRA> buffer, ImageRotation rotation);

    /// <inheritdoc cref="AbstractScreenCapture{TColor}.ReportError"/>
    void ReportError(Exception exception);
}
