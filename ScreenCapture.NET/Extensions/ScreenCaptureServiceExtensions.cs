using System;

namespace ScreenCapture.NET;

/// <summary>
/// Finds graphics cards and displays of a <see cref="IScreenCaptureService"/> by their id, e.g. one stored in settings.
/// </summary>
/// <remarks>
/// The devices are enumerated on every call, so the returned values are current (e.g. their <see cref="Display.Index"/>, size and position).
/// Ids are compared ordinally (case-sensitive). An id can stop matching, e.g. after a monitor was connected to another port
/// (see <see cref="GraphicsCard.Id"/> and <see cref="Display.Id"/>); falling back to another property, like <see cref="Display.FriendlyName"/>, is up to the caller.
/// Errors of the enumeration (e.g. no X server on X11) aren't caught.
/// </remarks>
public static class ScreenCaptureServiceExtensions
{
    #region Methods

    /// <summary>
    /// Finds the graphics card with the given <see cref="GraphicsCard.Id"/>.
    /// </summary>
    /// <param name="service">The service to search.</param>
    /// <param name="id">The id of the graphics card.</param>
    /// <param name="graphicsCard">The graphics card if it was found; otherwise <c>default</c>.</param>
    /// <returns><c>true</c> if the graphics card was found; otherwise, <c>false</c>.</returns>
    public static bool TryGetGraphicsCard(this IScreenCaptureService service, string id, out GraphicsCard graphicsCard)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(id);

        foreach (GraphicsCard card in service.GetGraphicsCards())
            if (string.Equals(card.Id, id, StringComparison.Ordinal))
            {
                graphicsCard = card;
                return true;
            }

        graphicsCard = default;
        return false;
    }

    /// <summary>
    /// Finds the display with the given <see cref="Display.Id"/> on any graphics card.
    /// </summary>
    /// <remarks>
    /// Display ids are unique across all graphics cards, so the id of the graphics card isn't needed.
    /// In clone mode the cloned monitors are one display, whose id is the one of the monitor Windows lists first; the other monitors' ids aren't found.
    /// </remarks>
    /// <param name="service">The service to search.</param>
    /// <param name="id">The id of the display.</param>
    /// <param name="display">The display if it was found; otherwise <c>default</c>.</param>
    /// <returns><c>true</c> if the display was found; otherwise, <c>false</c>.</returns>
    public static bool TryGetDisplay(this IScreenCaptureService service, string id, out Display display)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(id);

        foreach (GraphicsCard graphicsCard in service.GetGraphicsCards())
            foreach (Display candidate in service.GetDisplays(graphicsCard))
                if (string.Equals(candidate.Id, id, StringComparison.Ordinal))
                {
                    display = candidate;
                    return true;
                }

        display = default;
        return false;
    }

    #endregion
}
