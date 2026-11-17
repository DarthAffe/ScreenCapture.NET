// ReSharper disable MemberCanBePrivate.Global

using System.Diagnostics.CodeAnalysis;

namespace ScreenCapture.NET;

/// <summary>
/// Represents the information supplied with an <see cref="IScreenCapture.DisplayChanged" />-event.
/// </summary>
/// <param name="PreviousDisplay">The display before the change (the one of the previous event, or the one the capture was created with).</param>
/// <param name="Display">The display after the change, as <see cref="IScreenCapture.Display"/> reports it now. It has the same <see cref="ScreenCapture.NET.Display.Id"/>.</param>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "It is the argument of the DisplayChanged event. A struct can't derive from EventArgs, which EventHandler<T> doesn't require anymore.")]
public readonly record struct ScreenCaptureDisplayChangedEventArgs(Display PreviousDisplay, Display Display);
