using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ScreenCapture.NET.Viewer;

/// <summary>
/// The UI of a zone the user added: its rectangle on the preview (in display coordinates) and its card with the captured image in the zone list.
/// </summary>
internal sealed class ZoneView : IDisposable
{
    #region Properties & Fields

    private static readonly Color[] Colors =
    [
        Color.FromRgb(0xE6, 0x4B, 0x3C), Color.FromRgb(0x2E, 0xCC, 0x71), Color.FromRgb(0x34, 0x98, 0xDB), Color.FromRgb(0xF1, 0xC4, 0x0F),
        Color.FromRgb(0x9B, 0x59, 0xB6), Color.FromRgb(0x1A, 0xBC, 0x9C), Color.FromRgb(0xE6, 0x7E, 0x22), Color.FromRgb(0xEC, 0x40, 0x7A)
    ];

    private readonly Image _image;
    private readonly TextBlock _label;
    private WriteableBitmap? _bitmap;

    public ZoneMirror Mirror { get; }

    public int Number { get; }

    /// <summary>
    /// Gets the rectangle of the zone on the overlay of the preview.
    /// </summary>
    public Rectangle Overlay { get; }

    /// <summary>
    /// Gets the card of the zone in the zone list.
    /// </summary>
    public Border Card { get; }

    #endregion

    #region Constructors

    public ZoneView(ZoneMirror mirror, int number)
    {
        Mirror = mirror;
        Number = number;

        IBrush brush = new SolidColorBrush(Colors[(number - 1) % Colors.Length]);
        Overlay = new Rectangle { Stroke = brush, IsHitTestVisible = false };

        _image = new Image { Stretch = Stretch.Uniform, MaxHeight = 220, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.None);
        _label = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };

        Card = new Border
        {
            BorderBrush = brush,
            // Without a background only the image and the label take clicks.
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(2),
            Padding = new Avalonia.Thickness(6),
            Child = new StackPanel { Spacing = 4, Children = { _label, _image } }
        };
    }

    #endregion

    #region Methods

    public void UpdateFrame()
    {
        if (Mirror.UpdateBitmap(_bitmap) is not { } bitmap) return;

        if (!ReferenceEquals(bitmap, _bitmap))
        {
            _bitmap?.Dispose();
            _bitmap = bitmap;
            _image.Source = bitmap;
        }

        _image.InvalidateVisual();
    }

    /// <param name="strokeThickness">The thickness of the outline in display pixels (the overlay is scaled with the preview).</param>
    /// <param name="isSelected">Whether the zone is selected.</param>
    public void UpdateOverlay(double strokeThickness, bool isSelected)
    {
        ICaptureZone zone = Mirror.Zone;
        CaptureRegion region = zone.Region;

        Canvas.SetLeft(Overlay, region.X);
        Canvas.SetTop(Overlay, region.Y);
        Overlay.Width = region.Width;
        Overlay.Height = region.Height;
        Overlay.StrokeThickness = isSelected ? strokeThickness * 2 : strokeThickness;

        // Zones that don't lie within the display anymore (e.g. after a resolution change) aren't captured.
        Overlay.StrokeDashArray = zone.IsValid ? null : new AvaloniaList<double>(4, 2);
        Card.Opacity = zone.IsValid ? 1 : 0.6;
    }

    public void UpdateLabel()
    {
        ICaptureZone zone = Mirror.Zone;
        CaptureRegion region = zone.Region;
        _label.Text = $"#{Number}  {region.Width}x{region.Height} at {region.X},{region.Y}, level {zone.DownscaleLevel}"
                    + $" → {zone.Width}x{zone.Height}, stride {zone.Stride}{(zone.IsValid ? "" : "  (invalid: not within the display)")}";
    }

    public void Dispose() => _bitmap?.Dispose();

    #endregion
}
