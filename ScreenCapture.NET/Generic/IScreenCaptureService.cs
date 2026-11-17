using System;
using System.Collections.Generic;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a service that enumerates graphics cards and displays and creates <see cref="IScreenCapture"/> instances for them.
/// </summary>
/// <remarks>
/// The service owns the captures it creates: disposing the service disposes them.
/// <para>
/// <b>Thread safety:</b> all members can be used from any thread, also at the same time. <see cref="GetGraphicsCards"/> and <see cref="GetDisplays"/> read the devices
/// when they're called, so the results don't change while they're enumerated. <see cref="IDisposable.Dispose"/> can be called while other calls run:
/// they finish, and the methods called afterwards throw an <see cref="ObjectDisposedException"/>.
/// </para>
/// </remarks>
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
    /// <para>
    /// Concurrent calls for the same display get the same capture. A capture that was disposed is replaced by a new one, so disposing a capture
    /// and asking for it again restarts it from scratch (its zones aren't carried over).
    /// </para>
    /// </remarks>
    /// <param name="display">The display to duplicate.</param>
    /// <returns>The <see cref="IScreenCapture"/> for the given display.</returns>
    /// <exception cref="ObjectDisposedException">The service is disposed.</exception>
    IScreenCapture GetScreenCapture(Display display);
}