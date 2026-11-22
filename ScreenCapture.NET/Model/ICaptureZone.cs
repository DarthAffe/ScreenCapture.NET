using System;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a zone on the screen to be captured.
/// </summary>
/// <remarks>
/// <b>Thread safety:</b> all members can be used from any thread, also while the zone is captured or changed (see <see cref="IScreenCapture"/>).
/// <list type="bullet">
/// <item><description>
/// Every property is consistent on its own, but two properties read one after another can belong to different changes.
/// Use the size of a frame (see <see cref="AcquireFrame"/>), it doesn't change while the frame is held.
/// </description></item>
/// <item><description>
/// A frame is used and disposed on the thread that acquired it. Any number of frames can be held at once without blocking anything.
/// </description></item>
/// <item><description>
/// <see cref="Updated"/> is raised on the thread of the capture, without a lock of the library held.
/// </description></item>
/// <item><description>
/// <see cref="RequestUpdate"/> can be called at any time. A request made while the zone is written is kept for the next capture.
/// </description></item>
/// </list>
/// </remarks>
public interface ICaptureZone
{
    /// <summary>
    /// Gets the display this zone is on.
    /// </summary>
    /// <remarks>
    /// Follows the <see cref="IScreenCapture.Display"/> of the capture.
    /// </remarks>
    Display Display { get; }

    /// <summary>
    /// Gets if the <see cref="Region"/> lies within the <see cref="Display"/>, so the zone can be captured.
    /// </summary>
    /// <remarks>
    /// A zone becomes invalid if the display gets too small for its region (see <see cref="IScreenCapture.DisplayChanged"/>).
    /// An invalid zone isn't written and keeps its pixels, until <see cref="IScreenCapture.UpdateCaptureZone"/> gives it a region that fits or the display grows again.
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
    /// Changed with <see cref="IScreenCapture.UpdateCaptureZone"/>.
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
    /// Can be lower than the requested level: downscaling stops once both sides are down to 1 pixel.
    /// </remarks>
    int DownscaleLevel { get; }

    /// <summary>
    /// Gets or sets if the <see cref="ICaptureZone"/> should be automatically updated on every captured frame.
    /// </summary>
    /// <remarks>
    /// Captures without a new frame don't write the zone again, unless it doesn't show the last frame yet (e.g. it was changed or skipped a capture).
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
    /// Acquires the current pixels of the zone. Release them by disposing the returned frame.
    /// </summary>
    /// <remarks>
    /// Returns right away with the pixels the last capture wrote. They don't change while the frame is held: the zone is double-buffered.
    /// A zone whose frame is still held two captures later skips captures until it's disposed, so keep frames short.
    /// <para>
    /// Any method of the <see cref="IScreenCapture"/> can be called while holding a frame. The frame keeps its size.
    /// </para>
    /// </remarks>
    /// <returns>The frame holding the pixels.</returns>
    CaptureFrame AcquireFrame();

    /// <summary>
    /// Requests to update this <see cref="ICaptureZone"/> with the next capture, also if there's no new frame.
    /// Only necessary if <see cref="AutoUpdate"/> is set to <c>false</c>.
    /// </summary>
    /// <remarks>
    /// The request stays until the zone is written.
    /// </remarks>
    void RequestUpdate();
}
