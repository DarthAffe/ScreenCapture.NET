using System;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a zone on the screen to be captured.
/// </summary>
public interface ICaptureZone
{
    /// <summary>
    /// Gets the display this zone is on.
    /// </summary>
    /// <remarks>
    /// Follows the <see cref="IScreenCapture.Display"/> of the capture when it changes (see <see cref="IScreenCapture.DisplayChanged"/>).
    /// </remarks>
    Display Display { get; }

    /// <summary>
    /// Gets if the <see cref="Region"/> lies within the <see cref="Display"/>, so the zone can be captured.
    /// </summary>
    /// <remarks>
    /// A zone is valid when it's registered. It becomes invalid if the display gets smaller (e.g. a lower resolution or a rotation, see <see cref="IScreenCapture.DisplayChanged"/>)
    /// so that the region doesn't fit anymore. The region isn't changed: an invalid zone isn't written and keeps its pixels
    /// until <see cref="IScreenCapture.UpdateCaptureZone"/> gives it a region that fits, or the display gets large enough again.
    /// </remarks>
    bool IsValid { get; }

    /// <summary>
    /// Gets the color-format used in the buffer of this zone.
    /// </summary>
    IColorFormat ColorFormat { get; }

    /// <summary>
    /// Gets the region of the display this zone captures, in display pixels (before downscaling).
    /// </summary>
    /// <remarks>
    /// Change it with <see cref="IScreenCapture.UpdateCaptureZone"/>, e.g. <c>screenCapture.UpdateCaptureZone(zone, zone.Region with { X = 100 })</c>.
    /// </remarks>
    CaptureRegion Region { get; }

    /// <summary>
    /// Gets the width of the captured pixels (the width of <see cref="Region"/> after downscaling).
    /// </summary>
    int Width { get; }

    /// <summary>
    /// Gets the height of the captured pixels (the height of <see cref="Region"/> after downscaling).
    /// </summary>
    int Height { get; }

    /// <summary>
    /// Gets the stride of the buffer.
    /// </summary>
    int Stride { get; }

    /// <summary>
    /// Gets the level of downscaling applied to the image of this region before copying to local memory. The calculation is (width and height)/2^downscaleLevel.
    /// </summary>
    /// <remarks>
    /// This can be lower than the requested level: downscaling stops once both sides of the region are down to 1 pixel.
    /// </remarks>
    int DownscaleLevel { get; }

    /// <summary>
    /// Gets if the capture writes a second buffer while frames of this zone are held, instead of waiting for them.
    /// </summary>
    /// <remarks>
    /// Set when the zone is registered. Double-buffered zones need twice the memory for their pixels.
    /// </remarks>
    bool IsDoubleBuffered { get; }

    /// <summary>
    /// Gets or sets if the <see cref="ICaptureZone"/> should be automatically updated on every captured frame.
    /// </summary>
    /// <remarks>
    /// Captures without a new frame (<see cref="CaptureResult.NoChange"/>, <see cref="CaptureResult.Timeout"/>) don't write the zone again,
    /// unless it doesn't show the last frame yet: it was registered or changed (<see cref="IScreenCapture.UpdateCaptureZone"/>) since,
    /// or it missed the frame because a frame of the zone was still held (see <see cref="Lock"/>). So it's never left behind on a static screen.
    /// Invalid zones (see <see cref="IsValid"/>) aren't written.
    /// </remarks>
    bool AutoUpdate { get; set; }

    /// <summary>
    /// Gets if an update for the <see cref="ICaptureZone"/> is requested (see <see cref="RequestUpdate"/>).
    /// </summary>
    bool IsUpdateRequested { get; }

    /// <summary>
    /// Occurs when the <see cref="ICaptureZone"/> was written by a capture.
    /// </summary>
    event EventHandler? Updated;

    /// <summary>
    /// Locks the current pixels of the zone. Release them by disposing the returned frame.
    /// </summary>
    /// <remarks>
    /// The pixels of the frame don't change while it's held.
    /// <para>
    /// Double-buffered zones (the default, see <see cref="IsDoubleBuffered"/>): the capture writes a second buffer and never waits for frames.
    /// A frame that's still held when the capture after the next one starts makes the zone skip that capture.
    /// </para>
    /// <para>
    /// Single-buffered zones: the capture waits until the frame is disposed, and <see cref="Lock"/> waits while the capture writes the zone.
    /// The lock is reentrant: a capture on the thread holding the frame writes the zone anyway, and the frame shows the new pixels.
    /// </para>
    /// <para>
    /// Any method of the <see cref="IScreenCapture"/> can be called while holding a frame, including <see cref="IScreenCapture.UpdateCaptureZone"/> for this zone.
    /// The frame keeps the size it was created with. Keep frames short anyway.
    /// </para>
    /// </remarks>
    /// <returns>The frame holding the pixels.</returns>
    CaptureFrame Lock();

    /// <summary>
    /// Requests to update this <see cref="ICaptureZone"/> with the next capture, also if it has no new frame (from the last frame, which is still current).
    /// Only necessary if <see cref="AutoUpdate"/> is set to <c>false</c>.
    /// </summary>
    /// <remarks>
    /// The request stays until the zone is written: captures without a frame (e.g. <see cref="CaptureResult.Unavailable"/>) don't write zones.
    /// </remarks>
    void RequestUpdate();
}
