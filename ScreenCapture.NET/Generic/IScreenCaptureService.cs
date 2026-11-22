using System;
using System.Collections.Generic;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a service that enumerates graphics cards and displays and creates <see cref="IScreenCapture"/> instances for them.
/// </summary>
/// <remarks>
/// Disposing the service disposes the captures it created.
/// <para>
/// <b>Thread safety:</b> all members can be used from any thread, also at the same time. <see cref="IDisposable.Dispose"/> can be called while other calls run;
/// methods called afterwards throw an <see cref="ObjectDisposedException"/>.
/// </para>
/// </remarks>
public interface IScreenCaptureService : IDisposable
{
    /// <summary>
    /// Gets all available graphics cards.
    /// </summary>
    /// <returns>An enumerable of all available graphics cards.</returns>
    IEnumerable<GraphicsCard> GetGraphicsCards();

    /// <summary>
    /// Gets all displays connected to the given graphics card.
    /// </summary>
    /// <remarks>
    /// The graphics card is found by its <see cref="GraphicsCard.Id"/>.
    /// </remarks>
    /// <param name="graphicsCard">The graphics card to get the displays from.</param>
    /// <returns>An enumerable of all displays connected to the graphics card. Empty if the graphics card isn't available anymore.</returns>
    IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard);

    /// <summary>
    /// Creates a <see cref="IScreenCapture"/> for the given display.
    /// </summary>
    /// <remarks>
    /// There's one capture per <see cref="Display.Id"/>, returned again also if other values of the display changed.
    /// A disposed capture is replaced by a new one, without its zones.
    /// </remarks>
    /// <param name="display">The display to duplicate.</param>
    /// <returns>The <see cref="IScreenCapture"/> for the given display.</returns>
    /// <exception cref="ObjectDisposedException">The service is disposed.</exception>
    IScreenCapture GetScreenCapture(Display display);
}