// ReSharper disable MemberCanBePrivate.Global

using System.Diagnostics.CodeAnalysis;

namespace ScreenCapture.NET;

/// <summary>
/// Represents the information supplied with an <see cref="IScreenCapture.DisplayChanged" />-event.
/// </summary>
/// <param name="PreviousDisplay">The display before the change.</param>
/// <param name="Display">The display after the change.</param>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Event argument; a struct can't derive from EventArgs.")]
public readonly record struct ScreenCaptureDisplayChangedEventArgs(Display PreviousDisplay, Display Display);
