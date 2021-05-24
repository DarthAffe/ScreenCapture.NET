using System;
using System.Collections.Generic;

namespace ScreenCapture
{
    /// <summary>
    /// 
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
        /// <param name="graphicsCard">The graphics-card to get the displays from.</param>
        /// <returns>A enumerable of all display connected to the given graphics-cards.</returns>
        IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard);

        /// <summary>
        /// Creates a <see cref="IScreenCapture"/> for the given display.
        /// </summary>
        /// <param name="display">The display to duplicate.</param>
        /// <returns>The <see cref="IScreenCapture"/> for the give display.</returns>
        IScreenCapture GetScreenCapture(Display display);
    }
}
