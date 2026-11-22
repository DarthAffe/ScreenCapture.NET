using System.Text;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ScreenCapture.NET.Viewer;

public sealed partial class MainWindow : Window
{
    #region Types

    private enum DragMode { None, Create, Move, Resize }

    [Flags]
    private enum Edges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    #endregion

    #region Properties & Fields

    // How close to an edge (in screen pixels) a press resizes the zone instead of moving it.
    private const double EDGE_TOLERANCE = 6;

    // The outline of the zones in screen pixels.
    private const double STROKE_THICKNESS = 1.5;

    // The log keeps this many lines.
    private const int MAX_LOG_LINES = 1000;

    private readonly DispatcherTimer _statsTimer;
    private readonly AvaloniaList<string> _log = [];

    // Top, bottom, left and right black bar on the preview.
    private readonly Rectangle[] _blackBars = new Rectangle[4];
    private readonly List<ZoneView> _zoneViews = [];

    private IScreenCaptureService? _service;
    private CaptureSession? _session;
    private WriteableBitmap? _previewBitmap;

    // The zones of the last session, added again to the next one (e.g. to compare backends on the same zones).
    private List<(CaptureRegion Region, int DownscaleLevel)> _savedZones = [];
    private int _nextZoneNumber = 1;
    private ZoneView? _selectedZone;

    private DragMode _dragMode;
    private Edges _dragEdges;
    private Point _dragStart;
    private CaptureRegion _dragStartRegion;
    private ZoneView? _dragZone;

    // Set while a frame update is posted to the UI thread, so a slow UI gets one update per render instead of a queue.
    private int _framePosted;

    private bool _isBusy;

    #endregion

    #region Constructors

    public MainWindow()
    {
        InitializeComponent();

        BackendBox.SelectionChanged += (_, _) => _ = SelectBackendAsync();
        // A Wayland session gets its options when it starts: Refresh starts a new one (without the dialog if the token is stored).
        RefreshButton.Click += (_, _) => _ = (BackendBox.SelectedItem as string) == Backends.Wayland ? SelectBackendAsync() : RefreshSourcesAsync();
        StartButton.Click += (_, _) => ToggleCapture();
        RestartButton.Click += (_, _) => _session?.Restart();
        FrameRateSlider.ValueChanged += (_, _) => ApplyFrameRate();
        UnlimitedBox.IsCheckedChanged += (_, _) => ApplyFrameRate();
        LogList.ItemsSource = _log;
        ForgetTokenButton.Click += (_, _) =>
        {
            Backends.ForgetRestoreToken();
            AddLog("Wayland restore token deleted: the next session shows the dialog");
        };

        BlackBarsBox.IsCheckedChanged += (_, _) => ApplyBlackBarThreshold();
        BlackBarThresholdBox.ValueChanged += (_, _) => ApplyBlackBarThreshold();
        for (int i = 0; i < _blackBars.Length; i++)
        {
            _blackBars[i] = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0x00, 0xFF)) };
            BlackBarCanvas.Children.Add(_blackBars[i]);
        }
        ClearLogButton.Click += (_, _) => _log.Clear();
        SourceBox.SelectionChanged += (_, _) => UpdateControls();

        ApplyZoneButton.Click += (_, _) => ApplyZoneProperties();
        RemoveZoneButton.Click += (_, _) => RemoveSelectedZone();
        KeyDown += OnKeyDown;

        OverlayCanvas.PointerPressed += OnOverlayPointerPressed;
        OverlayCanvas.PointerMoved += OnOverlayPointerMoved;
        OverlayCanvas.PointerReleased += OnOverlayPointerReleased;
        PreviewBox.SizeChanged += (_, _) => UpdateOverlays();

        _statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ShowStats());
        UpdateControls();
    }

    #endregion

    #region Methods

    #region Backend & source

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        await RunBusyAsync("Checking which backends work on this system...", async () =>
        {
            List<string> backends = await Task.Run(Backends.GetSupported);
            BackendBox.ItemsSource = backends;
            StatusText.Text = backends.Count > 0 ? "" : "No backend works on this system.";
        });

        if (BackendBox.ItemCount > 0)
            BackendBox.SelectedIndex = 0;
    }

    private async Task SelectBackendAsync()
    {
        StopCapture();
        _service?.Dispose();
        _service = null;
        SourceBox.ItemsSource = null;

        if (BackendBox.SelectedItem is not string backend) return;

        await RunBusyAsync($"Starting {backend}...", async () =>
        {
            ViewerOptions options = CurrentOptions;
            _service = await Task.Run(() => Backends.Create(backend, options));
        });

        await RefreshSourcesAsync();
    }

    private async Task RefreshSourcesAsync()
    {
        if (_service is not { } service) return;

        // On Wayland the first GetDisplays shows the dialog of the desktop and waits for it.
        await RunBusyAsync("Getting the displays...", async () =>
        {
            List<CaptureSource> sources = await Task.Run(() => Backends.GetSources(service));

            CaptureSource? selected = SourceBox.SelectedItem as CaptureSource;
            SourceBox.ItemsSource = sources;
            SourceBox.SelectedItem = sources.FirstOrDefault(source => source == selected) ?? sources.FirstOrDefault();
            StatusText.Text = $"{sources.Count} sources";
        });
    }

    #endregion

    #region Capture

    private void ToggleCapture()
    {
        if (_session != null)
        {
            StopCapture();
            StatusText.Text = "Stopped";
        }
        else if ((_service != null) && (SourceBox.SelectedItem is CaptureSource source))
        {
            ViewerOptions options = CurrentOptions;
            try
            {
                _session = new CaptureSession(Backends.GetCapture(_service, source, options), TargetFrameRate);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Couldn't start the capture: {ex.Message}";
                UpdateControls();
                return;
            }

            _session.FrameReady += OnFrameReady;
            _session.DisplayChanged += OnDisplayChanged;
            _session.ErrorOccurred += OnErrorOccurred;
            _session.Log += OnLog;
            AddLog($"Started {BackendBox.SelectedItem}: {source.Name}, {options}");
            ApplyBlackBarThreshold();

            ShowDisplay(_session.ScreenCapture.Display);
            RestoreZones();
            _statsTimer.Start();
            StatusText.Text = $"Capturing {source.Name}";
        }

        UpdateControls();
    }

    private void StopCapture()
    {
        _statsTimer.Stop();

        if (_session != null)
        {
            AddLog("Stopped");
            _savedZones = [.. _session.Zones.Select(zone => (zone.Zone.Region, zone.Zone.DownscaleLevel))];
            _session.Dispose();
            _session = null;
        }

        foreach (ZoneView view in _zoneViews)
            view.Dispose();

        _zoneViews.Clear();
        ZoneList.Children.Clear();
        OverlayCanvas.Children.Clear();
        SelectZone(null);
        DisplaySurface.IsVisible = false;
        StatsText.Text = "Not capturing";
        UpdateControls();
    }

    // Zones that don't lie within the new source are left out.
    private void RestoreZones()
    {
        int skipped = 0;
        foreach ((CaptureRegion region, int downscaleLevel) in _savedZones)
        {
            try
            {
                AddZone(region, downscaleLevel);
            }
            catch (ArgumentException)
            {
                skipped++;
            }
        }

        if (skipped > 0)
            StatusText.Text = $"{skipped} zones don't lie within this source and were left out.";
    }

    // On the thread of the loop.
    private void OnFrameReady()
    {
        if (Interlocked.Exchange(ref _framePosted, 1) == 0)
            Dispatcher.UIThread.Post(ShowFrames, DispatcherPriority.Render);
    }

    private void ShowFrames()
    {
        Volatile.Write(ref _framePosted, 0);
        if (_session is not { } session) return;

        if (session.Preview.UpdateBitmap(_previewBitmap) is { } bitmap)
        {
            if (!ReferenceEquals(bitmap, _previewBitmap))
            {
                _previewBitmap?.Dispose();
                _previewBitmap = bitmap;
                PreviewImage.Source = bitmap;
            }

            PreviewImage.InvalidateVisual();
            ShowBlackBars();
        }

        foreach (ZoneView view in _zoneViews)
        {
            view.UpdateFrame();
            view.UpdateLabel();
        }
    }

    // On the thread of the loop.
    private void OnDisplayChanged(Display display)
        => Dispatcher.UIThread.Post(() =>
        {
            if (_session == null) return;

            ShowDisplay(display);
            foreach (ZoneView view in _zoneViews)
                view.UpdateLabel();

            StatusText.Text = $"Display changed: {Backends.Describe(display)}";
        });

    // On the thread of the loop.
    private void OnErrorOccurred(Exception exception)
        => Dispatcher.UIThread.Post(() => StatusText.Text = $"Error: {exception.Message}");

    private void ShowDisplay(Display display)
    {
        DisplaySurface.Width = display.Width;
        DisplaySurface.Height = display.Height;
        DisplaySurface.IsVisible = true;
        UpdateOverlays();
    }

    private void ShowStats()
    {
        if (_session is not { } session) return;

        CaptureStats stats = session.TakeStats();
        StringBuilder text = new();
        text.AppendLine($"{"Result",-12}{"last s",8}{"total",10}");
        foreach (CaptureResult result in Enum.GetValues<CaptureResult>())
            text.AppendLine($"{result,-12}{stats.Results[(int)result],8}{stats.TotalResults[(int)result],10}");

        text.AppendLine();
        text.AppendLine($"Frame number   {stats.FrameNumber}");
        // The time since the frame's timestamp when the capture returned it: negative with WGC, whose timestamp is when the frame will be shown.
        text.AppendLine($"Frame age      {Milliseconds(stats.MinAge)} / {Milliseconds(stats.AverageAge)} / {Milliseconds(stats.MaxAge)} ms (min/avg/max)");
        text.AppendLine($"Interval       {Milliseconds(stats.AverageInterval)} / {Milliseconds(stats.MaxInterval)} ms (avg/max)");
        text.AppendLine($"Display        {Backends.Describe(session.ScreenCapture.Display)}");
        if (BlackBarsInDisplayPixels() is { } bars)
            text.Append($"Black bars     top {bars.Top}, bottom {bars.Bottom}, left {bars.Left}, right {bars.Right} px");
        StatsText.Text = text.ToString();

        static string Milliseconds(TimeSpan? value) => value is { } time ? time.TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "-";
    }

    // On the thread of the loop (or the UI thread for a restart).
    private void OnLog(string line)
    {
        string stamped = $"{DateTime.Now:HH:mm:ss.fff}  {line}";
        Dispatcher.UIThread.Post(() => AddLog(stamped, stampedAlready: true));
    }

    private void AddLog(string line, bool stampedAlready = false)
    {
        _log.Add(stampedAlready ? line : $"{DateTime.Now:HH:mm:ss.fff}  {line}");
        if (_log.Count > MAX_LOG_LINES)
            _log.RemoveRange(0, _log.Count - MAX_LOG_LINES);

        LogList.ScrollIntoView(_log.Count - 1);
    }

    private ViewerOptions CurrentOptions => new(TimeSpan.FromMilliseconds((double)(TimeoutBox.Value ?? 1000)), CaptureHdrBox.IsChecked == true, DrawCursorBox.IsChecked == true,
                                                ShowBorderBox.IsChecked == true, SharedMemoryBox.IsChecked == true, Math.Max(0, PersistModeBox.SelectedIndex));

    // The detection runs on the preview zone (on the thread of the loop) and can be changed while capturing.
    private void ApplyBlackBarThreshold()
    {
        BlackBarThresholdBox.IsEnabled = BlackBarsBox.IsChecked == true;
        if (_session != null)
            _session.Preview.BlackBarThreshold = BlackBarsBox.IsChecked == true ? (int)(BlackBarThresholdBox.Value ?? 8) : -1;

        if (BlackBarsBox.IsChecked != true)
            BlackBarCanvas.IsVisible = false;
    }

    private void ShowBlackBars()
    {
        if ((BlackBarsBox.IsChecked != true) || (_session?.Preview.BlackBars is not { } bars) || (bars.Width == 0) || (bars.Height == 0))
        {
            BlackBarCanvas.IsVisible = false;
            return;
        }

        // The detection ran on the downscaled preview: scale it to display coordinates.
        CaptureRegion region = _session.Preview.Zone.Region;
        double scaleX = region.Width / (double)bars.Width;
        double scaleY = region.Height / (double)bars.Height;
        double contentTop = bars.Top * scaleY, contentBottom = bars.Bottom * scaleY;

        Place(_blackBars[0], 0, 0, region.Width, contentTop);
        Place(_blackBars[1], 0, contentBottom, region.Width, region.Height - contentBottom);
        Place(_blackBars[2], 0, contentTop, bars.Left * scaleX, contentBottom - contentTop);
        Place(_blackBars[3], bars.Right * scaleX, contentTop, region.Width - (bars.Right * scaleX), contentBottom - contentTop);
        BlackBarCanvas.IsVisible = true;

        static void Place(Rectangle rectangle, double x, double y, double width, double height)
        {
            Canvas.SetLeft(rectangle, x);
            Canvas.SetTop(rectangle, y);
            rectangle.Width = Math.Max(0, width);
            rectangle.Height = Math.Max(0, height);
        }
    }

    // The sizes of the four bars in display pixels.
    private (int Top, int Bottom, int Left, int Right)? BlackBarsInDisplayPixels()
    {
        if ((BlackBarsBox.IsChecked != true) || (_session?.Preview.BlackBars is not { } bars) || (bars.Width == 0) || (bars.Height == 0)) return null;

        CaptureRegion region = _session.Preview.Zone.Region;
        double scaleX = region.Width / (double)bars.Width;
        double scaleY = region.Height / (double)bars.Height;
        return ((int)Math.Round(bars.Top * scaleY), (int)Math.Round((bars.Height - bars.Bottom) * scaleY),
                (int)Math.Round(bars.Left * scaleX), (int)Math.Round((bars.Width - bars.Right) * scaleX));
    }

    private double? TargetFrameRate => UnlimitedBox.IsChecked == true ? null : FrameRateSlider.Value;

    // The loop picks a new rate up with the next capture, also while it waits.
    private void ApplyFrameRate()
    {
        FrameRateText.Text = UnlimitedBox.IsChecked == true ? "-" : $"{FrameRateSlider.Value:0}";
        FrameRateSlider.IsEnabled = UnlimitedBox.IsChecked != true;

        if (_session != null)
            _session.TargetFrameRate = TargetFrameRate;
    }

    #endregion

    #region Zones

    private ZoneView AddZone(CaptureRegion region, int downscaleLevel)
    {
        ZoneMirror mirror = _session!.AddZone(region, downscaleLevel);
        ZoneView view = new(mirror, _nextZoneNumber++);
        view.Card.PointerPressed += (_, _) => SelectZone(view);

        _zoneViews.Add(view);
        OverlayCanvas.Children.Add(view.Overlay);
        ZoneList.Children.Add(view.Card);
        view.UpdateLabel();
        UpdateOverlays();
        return view;
    }

    private void RemoveSelectedZone()
    {
        if ((_session == null) || (_selectedZone is not { } view)) return;

        _session.RemoveZone(view.Mirror);
        _zoneViews.Remove(view);
        OverlayCanvas.Children.Remove(view.Overlay);
        ZoneList.Children.Remove(view.Card);
        view.Dispose();
        SelectZone(null);
    }

    private void UpdateZone(ZoneView view, CaptureRegion? region = null, int? downscaleLevel = null)
    {
        try
        {
            _session?.UpdateZone(view.Mirror, region, downscaleLevel);
        }
        catch (ArgumentException ex)
        {
            StatusText.Text = $"Couldn't change zone #{view.Number}: {ex.Message}";
        }

        view.UpdateLabel();
        UpdateOverlays();
        if (view == _selectedZone)
            ShowZoneProperties(view);
    }

    private void SelectZone(ZoneView? view)
    {
        _selectedZone = view;
        if (view != null)
            ShowZoneProperties(view);
        else
            SelectedZoneText.Text = "No zone selected";

        UpdateOverlays();
        UpdateControls();
    }

    private void ShowZoneProperties(ZoneView view)
    {
        CaptureRegion region = view.Mirror.Zone.Region;
        SelectedZoneText.Text = $"Zone #{view.Number}";
        ZoneXBox.Value = region.X;
        ZoneYBox.Value = region.Y;
        ZoneWidthBox.Value = region.Width;
        ZoneHeightBox.Value = region.Height;
        ZoneLevelBox.Value = view.Mirror.Zone.DownscaleLevel;
    }

    // The values are passed as they are, so the library's checks can be tried out (e.g. a zone that doesn't lie within the display).
    private void ApplyZoneProperties()
    {
        if (_selectedZone is not { } view) return;

        CaptureRegion region = new((int)(ZoneXBox.Value ?? 0), (int)(ZoneYBox.Value ?? 0), (int)(ZoneWidthBox.Value ?? 1), (int)(ZoneHeightBox.Value ?? 1));
        UpdateZone(view, region, (int)(ZoneLevelBox.Value ?? 0));
    }

    private void UpdateOverlays()
    {
        double thickness = STROKE_THICKNESS / Math.Max(PreviewScale(), 0.0001);
        foreach (ZoneView view in _zoneViews)
            view.UpdateOverlay(thickness, view == _selectedZone);
    }

    // Screen pixels per display pixel.
    private double PreviewScale()
    {
        if ((DisplaySurface.Width <= 0) || (DisplaySurface.Height <= 0) || double.IsNaN(DisplaySurface.Width)) return 1;
        return Math.Min(PreviewBox.Bounds.Width / DisplaySurface.Width, PreviewBox.Bounds.Height / DisplaySurface.Height);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Delete in a number box edits the number.
        if ((e.Key == Key.Delete) && (e.Source is not TextBox))
            RemoveSelectedZone();
    }

    #endregion

    #region Mouse editing

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((_session == null) || !e.GetCurrentPoint(OverlayCanvas).Properties.IsLeftButtonPressed) return;

        Point position = e.GetPosition(OverlayCanvas);
        (ZoneView? view, Edges edges) = HitTest(position);

        _dragStart = position;
        _dragZone = view;
        _dragEdges = edges;
        _dragMode = view == null ? DragMode.Create : (edges == Edges.None ? DragMode.Move : DragMode.Resize);
        if (view != null)
            _dragStartRegion = view.Mirror.Zone.Region;

        SelectZone(view);
        e.Pointer.Capture(OverlayCanvas);
        e.Handled = true;
    }

    private void OnOverlayPointerMoved(object? sender, PointerEventArgs e)
    {
        Point position = e.GetPosition(OverlayCanvas);

        if (_dragMode == DragMode.None)
        {
            (ZoneView? hovered, Edges edges) = HitTest(position);
            OverlayCanvas.Cursor = hovered == null ? new Cursor(StandardCursorType.Cross) : new Cursor(CursorFor(edges));
            return;
        }

        if (_session == null) return;

        Display display = _session.ScreenCapture.Display;
        int dx = (int)Math.Round(position.X - _dragStart.X);
        int dy = (int)Math.Round(position.Y - _dragStart.Y);

        switch (_dragMode)
        {
            case DragMode.Create:
                CaptureRegion created = Normalize(_dragStart, position, display);
                if (_dragZone == null)
                {
                    // Only a drag of a few pixels adds a zone, a click just clears the selection.
                    if ((created.Width < 4) || (created.Height < 4)) return;

                    _dragZone = AddZoneSafely(created, (int)(ZoneLevelBox.Value ?? 0));
                    if (_dragZone != null)
                        SelectZone(_dragZone);
                }
                else
                    UpdateZone(_dragZone, created);
                break;

            case DragMode.Move when _dragZone != null:
                CaptureRegion start = _dragStartRegion;
                int x = Math.Clamp(start.X + dx, 0, Math.Max(0, display.Width - start.Width));
                int y = Math.Clamp(start.Y + dy, 0, Math.Max(0, display.Height - start.Height));
                UpdateZone(_dragZone, start with { X = x, Y = y });
                break;

            case DragMode.Resize when _dragZone != null:
                UpdateZone(_dragZone, Resize(_dragStartRegion, _dragEdges, dx, dy, display));
                break;
        }
    }

    private void OnOverlayPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragMode = DragMode.None;
        _dragZone = null;
        e.Pointer.Capture(null);
    }

    private ZoneView? AddZoneSafely(CaptureRegion region, int downscaleLevel)
    {
        try
        {
            return AddZone(region, downscaleLevel);
        }
        catch (ArgumentException ex)
        {
            StatusText.Text = $"Couldn't add the zone: {ex.Message}";
            return null;
        }
    }

    // The topmost zone (added last) under the position, and which of its edges are within reach.
    private (ZoneView? View, Edges Edges) HitTest(Point position)
    {
        double tolerance = EDGE_TOLERANCE / Math.Max(PreviewScale(), 0.0001);

        for (int i = _zoneViews.Count - 1; i >= 0; i--)
        {
            CaptureRegion region = _zoneViews[i].Mirror.Zone.Region;
            double left = region.X, top = region.Y, right = region.X + region.Width, bottom = region.Y + region.Height;

            if ((position.X < (left - tolerance)) || (position.X > (right + tolerance)) || (position.Y < (top - tolerance)) || (position.Y > (bottom + tolerance)))
                continue;

            Edges edges = Edges.None;
            if (Math.Abs(position.X - left) <= tolerance) edges |= Edges.Left;
            else if (Math.Abs(position.X - right) <= tolerance) edges |= Edges.Right;
            if (Math.Abs(position.Y - top) <= tolerance) edges |= Edges.Top;
            else if (Math.Abs(position.Y - bottom) <= tolerance) edges |= Edges.Bottom;

            return (_zoneViews[i], edges);
        }

        return (null, Edges.None);
    }

    private static CaptureRegion Normalize(Point a, Point b, Display display)
    {
        int left = Math.Clamp((int)Math.Round(Math.Min(a.X, b.X)), 0, display.Width - 1);
        int top = Math.Clamp((int)Math.Round(Math.Min(a.Y, b.Y)), 0, display.Height - 1);
        int right = Math.Clamp((int)Math.Round(Math.Max(a.X, b.X)), left + 1, display.Width);
        int bottom = Math.Clamp((int)Math.Round(Math.Max(a.Y, b.Y)), top + 1, display.Height);
        return new CaptureRegion(left, top, right - left, bottom - top);
    }

    private static CaptureRegion Resize(CaptureRegion start, Edges edges, int dx, int dy, Display display)
    {
        int left = start.X, top = start.Y, right = start.X + start.Width, bottom = start.Y + start.Height;

        if (edges.HasFlag(Edges.Left)) left = Math.Clamp(left + dx, 0, right - 1);
        if (edges.HasFlag(Edges.Right)) right = Math.Clamp(right + dx, left + 1, display.Width);
        if (edges.HasFlag(Edges.Top)) top = Math.Clamp(top + dy, 0, bottom - 1);
        if (edges.HasFlag(Edges.Bottom)) bottom = Math.Clamp(bottom + dy, top + 1, display.Height);

        return new CaptureRegion(left, top, right - left, bottom - top);
    }

    private static StandardCursorType CursorFor(Edges edges) => edges switch
    {
        Edges.Left | Edges.Top => StandardCursorType.TopLeftCorner,
        Edges.Right | Edges.Top => StandardCursorType.TopRightCorner,
        Edges.Left | Edges.Bottom => StandardCursorType.BottomLeftCorner,
        Edges.Right | Edges.Bottom => StandardCursorType.BottomRightCorner,
        Edges.Left => StandardCursorType.LeftSide,
        Edges.Right => StandardCursorType.RightSide,
        Edges.Top => StandardCursorType.TopSide,
        Edges.Bottom => StandardCursorType.BottomSide,
        _ => StandardCursorType.SizeAll
    };

    #endregion

    #region UI state

    private async Task RunBusyAsync(string status, Func<Task> action)
    {
        _isBusy = true;
        StatusText.Text = status;
        UpdateControls();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _isBusy = false;
            UpdateControls();
        }
    }

    private void UpdateControls()
    {
        bool capturing = _session != null;
        bool hasZone = capturing && (_selectedZone != null);

        BackendBox.IsEnabled = !_isBusy;
        SourceBox.IsEnabled = !_isBusy && !capturing;
        RefreshButton.IsEnabled = !_isBusy && !capturing && (_service != null);
        StartButton.IsEnabled = !_isBusy && (capturing || (SourceBox.SelectedItem != null));
        StartButton.Content = capturing ? "Stop" : "Start";
        RestartButton.IsEnabled = capturing;

        // Each backend only offers the options it has.
        string? backend = BackendBox.SelectedItem as string;
        bool wgc = backend == Backends.WGC, dx11 = backend == Backends.DX11, wayland = backend == Backends.Wayland;
        TimeoutBox.IsEnabled = wgc || dx11 || wayland;
        CaptureHdrBox.IsEnabled = wgc || dx11;
        DrawCursorBox.IsEnabled = wgc || dx11 || wayland;
        ShowBorderBox.IsEnabled = wgc;
        SharedMemoryBox.IsEnabled = backend == Backends.X11;
        PersistModeBox.IsEnabled = ForgetTokenButton.IsEnabled = wayland;

        ZoneXBox.IsEnabled = ZoneYBox.IsEnabled = ZoneWidthBox.IsEnabled = ZoneHeightBox.IsEnabled = hasZone;
        ApplyZoneButton.IsEnabled = RemoveZoneButton.IsEnabled = hasZone;
    }

    protected override void OnClosed(EventArgs e)
    {
        StopCapture();
        _service?.Dispose();
        _previewBitmap?.Dispose();
        base.OnClosed(e);
    }

    #endregion

    #endregion
}
