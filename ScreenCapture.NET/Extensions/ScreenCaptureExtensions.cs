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
    /// The async counterpart of <see cref="ScreenCaptureLoop"/>, with the same pacing and backoff.
    /// The next capture starts only when the consumer asks for it, so the zones aren't written while it handles a result.
    /// <code>
    /// await foreach (CaptureResult result in screenCapture.CaptureLoopAsync(60, cancellationToken))
    ///     if (result == CaptureResult.NewFrame)
    ///         using (CaptureFrame frame = zone.AcquireFrame())
    ///             ...
    /// </code>
    /// The captures run on the thread pool, so the events are raised there.
    /// <para>
    /// The sequence ends when the capture is disposed. Cancellation throws an <see cref="OperationCanceledException"/>,
    /// other exceptions of <see cref="IScreenCapture.CaptureScreen"/> end the sequence and are passed on.
    /// </para>
    /// </remarks>
    /// <param name="screenCapture">The capture.</param>
    /// <param name="targetFrameRate">The number of captures per second to aim for, or <c>null</c> for no limit (see <see cref="ScreenCaptureLoop.TargetFrameRate"/>).</param>
    /// <param name="cancellationToken">A token that ends the sequence.</param>
    /// <returns>The results of the captures.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="screenCapture"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetFrameRate"/> isn't a finite number greater than 0, or is less than one frame per day.</exception>
    public static IAsyncEnumerable<CaptureResult> CaptureLoopAsync(this IScreenCapture screenCapture, double? targetFrameRate = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(screenCapture);
        TimeSpan interval = CapturePacer.GetInterval(targetFrameRate, nameof(targetFrameRate));

        // Split, so the arguments are validated right away and not on the first enumeration.
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
