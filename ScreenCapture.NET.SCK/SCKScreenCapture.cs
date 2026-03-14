using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HPPH;
using ObjCRuntime;
using ScreenCapture.NET.SCK;
using ScreenCaptureKit;

namespace ScreenCapture.NET;
/// <summary>
/// Follow the architecture of this library created by author
/// This class create a session require for screen content (in mac os)
/// Capture screen for whatever interval (you have to implement a loop in calling class)
/// Then store at a buffer with the size calculated by color space and screen resolution
/// Everytime user register a new capture zone, a buffer corresponding for that zone is created
///
/// </summary>

public sealed class SCKScreenCapture : AbstractScreenCapture<ColorBGRA>
{
    private SCDisplay _selectedDisplay;
    private byte[]? _buffer;
    private int _stride;
    public double ScalingFactor => _scalingFactor;
    private double _scalingFactor = 0.25d;
    private SCContentFilter _filter;
    private SCStreamConfiguration _streamConfig;
    private SCStream _stream;
    private ScreenCaptureDelegate _delegate;
    public readonly object _captureLock = new();
    public int BufferReceived => _delegate.BufferReceived;
    private bool _isInitialized;
    private int _blackFrameCounter;
    private readonly object _lock = new();
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SCKScreenCapture"/> class.
    /// </summary>
    /// <param name="display">The <see cref="Display"/> to capture.</param>
    /// <param name="scalingFactor">The scale factor value to start new video stream at.</param>
    internal SCKScreenCapture(Display display, double scalingFactor)
        : base(display)
    {
        _scalingFactor = scalingFactor;
        Restart();
        OnStreamComplete += StreamComplete;
    }

    private void StreamComplete(NSError error)
    {
        if (error != null)
        {
            _isInitialized = false;
            _stream?.Dispose();
            Thread.Sleep(1000);
        }
        else
        {
            _isInitialized = true;
        }

    }

    public NativeHandle Handle { get; set; }

    #endregion

    /// <summary>
    /// Perform a clean restart
    /// </summary>
    public override void Restart()
    {
        base.Restart();
        lock (_captureLock)
        {
            try
            {
                SCShareableContent.GetShareableContent((SCShareableContent content, NSError error) =>
                            {
                                if (error != null)
                                {
                                    //this is when user press denied, so we stop requesting
                                    _isInitialized = true;
                                    return;
                                }

                                //check if the display exist, feel silly enough because it actually happens when mac
                                // just wakeup from sleep
                                //the display is not available yet but the code is already running in background
                                if (content.Displays.Length <= Display.Index)
                                {
                                    // Log.Error("Requested display not found" + Display.Index);
                                    return;
                                }
                                _selectedDisplay = content.Displays[Display.Index];
                                _stride = (int)(Display.Width * _scalingFactor * ColorBGRA.ColorFormat.BytesPerPixel);
                                _buffer = new byte[(int)(Display.Height * _scalingFactor * _stride)];
                                var apps = content.Applications;
                                //config new sreen capture session
                                _filter = new SCContentFilter(_selectedDisplay, [], SCContentFilterOption.Exclude);
                                _streamConfig = new SCStreamConfiguration
                                {
                                    Width = (nuint)(Display.Width * _scalingFactor),
                                    Height = (nuint)(Display.Height * _scalingFactor),
                                    MinimumFrameInterval = new CoreMedia.CMTime(1, 30), // 60 FPS
                                    QueueDepth = 5,
                                    PixelFormat = CoreVideo.CVPixelFormatType.CV32BGRA,
                                    ScalesToFit = false,
                                    SourceRect = new CGRect(0, 0, Display.Width, Display.Height),
                                    ShowsCursor = false,
                                    CaptureResolution = SCCaptureResolutionType.Best,
                                    CapturesAudio = false,
                                    StreamName = "SCKScreenCapture.NET"

                                };
                                //update registerd zones
                                _delegate = new ScreenCaptureDelegate(_buffer);
                                _delegate.StreamStopped += OnStreamStopped;
                                _stream = new SCStream(_filter, _streamConfig, _delegate);
                                var streamError = new NSError();
                                _stream.AddStreamOutput(_delegate, SCStreamOutputType.Screen, null, out streamError);
                                _stream.StartCapture(OnStreamComplete);

                            });

            }
            catch (Exception ex)
            {

            }
            finally
            {

            }

        }


    }
    private void OnStreamStopped()
    {
        _stream?.Dispose();
        Thread.Sleep(1000);
        _isInitialized = false;
    }
    private Action<NSError> OnStreamComplete;
    private bool ReadyToRestart()
    {
        lock (_lock)
        {
            return !_isInitialized;
        }
    }
    /// <inheritdoc />
    protected override void PerformCaptureZoneUpdate(CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        if (ReadyToRestart())
        {
            _blackFrameCounter++;
            if (_blackFrameCounter > 600)
            {
                //10 secs has passed, try to restart
                Restart();
                _blackFrameCounter = 0;
            }
        }

        if (_buffer == null) return;
        using IDisposable @lock = captureZone.Lock();
        {
            if (captureZone.DownscaleLevel == 0)
                CopyZone(captureZone, buffer);
            else
                DownscaleZone(captureZone, buffer);
        }
    }

    protected override bool PerformScreenCapture()
    {
        bool result = true;
        //this will be handled in PerformCaptureZoneUpdate();
        return result;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CopyZone(CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        RefImage<ColorBGRA>.Wrap(_buffer, (int)(Display.Width * _scalingFactor), (int)(Display.Height * _scalingFactor), _stride)[captureZone.X, captureZone.Y, captureZone.Width, captureZone.Height]
                           .CopyTo(MemoryMarshal.Cast<byte, ColorBGRA>(buffer));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DownscaleZone(CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        RefImage<ColorBGRA> source = RefImage<ColorBGRA>.Wrap(_buffer, (int)(Display.Width * _scalingFactor), (int)(Display.Height * _scalingFactor), _stride)[captureZone.X, captureZone.Y, captureZone.UnscaledWidth, captureZone.UnscaledHeight];
        Span<ColorBGRA> target = MemoryMarshal.Cast<byte, ColorBGRA>(buffer);

        int blockSize = 1 << captureZone.DownscaleLevel;

        int width = captureZone.Width;
        int height = captureZone.Height;

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                target[(y * width) + x] = source[x * blockSize, y * blockSize, blockSize, blockSize].Average();
    }

}
