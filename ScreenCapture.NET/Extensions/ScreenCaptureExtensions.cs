using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenCapture.NET;

/// <summary>
/// Extensions for <see cref="IScreenCapture"/>.
/// </summary>
public static class ScreenCaptureExtensions
{
    #region Methods

    /// <summary>
    /// Captures the screen continuously at a target frame rate, capturing the next frame when the consumer asks for it.
    /// </summary>
    /// <remarks>
    /// The async counterpart of <see cref="ScreenCaptureLoop"/>, with the same pacing (see <see cref="ScreenCaptureLoop.TargetFrameRate"/>) and backoff after
    /// <see cref="CaptureResult.Failed"/>. The next capture starts only when the consumer asks for the next result, so the zones aren't written while the consumer handles one
    /// (unless another thread captures). The time the consumer takes counts towards the interval.
    /// <code>
    /// await foreach (CaptureResult result in screenCapture.CaptureLoopAsync(60, cancellationToken))
    ///     if (result == CaptureResult.NewFrame)
    ///         using (CaptureFrame frame = zone.AcquireFrame())
    ///             ...
    /// </code>
    /// <see cref="IScreenCapture.CaptureScreen"/> runs on the thread pool, so the events of the capture and its zones are raised there.
    /// A capture can block its thread for up to the timeout of the capture (DX11, WGC and Wayland) or its restart backoff (up to 2 s).
    /// <para>
    /// The sequence ends when the capture is disposed. Cancellation throws an <see cref="OperationCanceledException"/>.
    /// Other exceptions thrown by <see cref="IScreenCapture.CaptureScreen"/> (which a capture is not supposed to throw) end the sequence and are thrown to the consumer.
    /// </para>
    /// </remarks>
    /// <param name="screenCapture">The capture.</param>
    /// <param name="targetFrameRate">The number of captures per second to aim for, or <c>null</c> to capture as soon as the consumer asks (see <see cref="ScreenCaptureLoop.TargetFrameRate"/>).</param>
    /// <param name="cancellationToken">A token that ends the sequence.</param>
    /// <returns>The results of the captures.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="screenCapture"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetFrameRate"/> isn't a finite number greater than 0, or is less than one frame per day.</exception>
    public static IAsyncEnumerable<CaptureResult> CaptureLoopAsync(this IScreenCapture screenCapture, double? targetFrameRate = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(screenCapture);
        TimeSpan interval = CapturePacer.GetInterval(targetFrameRate, nameof(targetFrameRate));

        // Validated above, when called, instead of when the sequence is enumerated.
        return CaptureLoopCoreAsync(screenCapture, interval, cancellationToken);
    }

    private static async IAsyncEnumerable<CaptureResult> CaptureLoopCoreAsync(IScreenCapture screenCapture, TimeSpan interval, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        CapturePacer pacer = new();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TimeSpan delay = pacer.GetDelay(Stopwatch.GetTimestamp(), interval);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(CapturePacer.ToWaitMilliseconds(delay), cancellationToken).ConfigureAwait(false);
                continue;
            }

            long started = Stopwatch.GetTimestamp();
            if (await Task.Run(() => ScreenCaptureLoop.TryCaptureScreen(screenCapture), cancellationToken).ConfigureAwait(false) is not { } result)
                yield break;

            pacer.Captured(started, Stopwatch.GetTimestamp(), result, interval);

            yield return result;
        }
    }

    #endregion
}
