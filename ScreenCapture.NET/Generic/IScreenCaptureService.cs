using System;
using System.Collections.Generic;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a service that enumerates graphics cards and displays and creates <see cref="IScreenCapture"/> instances for them.
/// </summary>
public interface IScreenCaptureService : IDisposable
{
    /// <summary>
    /// Gets a enumerable of all available graphics-cards.
    /// </summary>
    /// <returns>A enumerable of all available graphics-cards.</returns>
    IEnumerable<GraphicsCard> GetGraphicsCards();

    /// <summary>
    /// Gets a enumerable of all display connected to the given graphics-cards.
    /// </summary>
    /// <remarks>
    /// The graphics card is found by its <see cref="GraphicsCard.Id"/>.
    /// </remarks>
    /// <param name="graphicsCard">The graphics-card to get the displays from.</param>
    /// <returns>A enumerable of all display connected to the given graphics-cards. Empty if the graphics card isn't available anymore.</returns>
    IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard);

    /// <summary>
    /// Creates a <see cref="IScreenCapture"/> for the given display.
    /// </summary>
    /// <remarks>
    /// The service creates one capture per display and returns it again for every display with the same <see cref="Display.Id"/>, also if other values of the display changed.
    /// The capture keeps the values of the display it was created with.
    /// </remarks>
    /// <param name="display">The display to duplicate.</param>
    /// <returns>The <see cref="IScreenCapture"/> for the give display.</returns>
    IScreenCapture GetScreenCapture(Display display);
}