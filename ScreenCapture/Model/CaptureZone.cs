using System;

namespace ScreenCapture
{
    public sealed class CaptureZone
    {
        #region Properties & Fields

        public int Id { get; }

        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }

        public int DownscaleLevel { get; }

        public int UnscaledWidth { get; }
        public int UnscaledHeight { get; }

        public int CaptureWidth { get; }
        public int CaptureHeight { get; }

        public int BufferWidth { get; }
        public int BufferHeight { get; }
        public byte[] Buffer { get; }

        public BlackBarDetection BlackBars { get; }

        public bool AutoUpdate { get; set; } = true;
        public bool IsUpdateRequested { get; private set; }

        #endregion

        #region Events

        public event EventHandler? Updated;

        #endregion

        #region Constructors

        public CaptureZone(int id, int x, int y, int width, int height, int downscaleLevel, int unscaledWidth, int unscaledHeight, int captureWidth, int captureHeight, int bufferWidth, int bufferHeight, byte[] buffer)
        {
            this.Id = id;
            this.X = x;
            this.Y = y;
            this.Width = width;
            this.Height = height;
            this.UnscaledWidth = unscaledWidth;
            this.UnscaledHeight = unscaledHeight;
            this.DownscaleLevel = downscaleLevel;
            this.CaptureWidth = captureWidth;
            this.CaptureHeight = captureHeight;
            this.BufferWidth = bufferWidth;
            this.BufferHeight = bufferHeight;
            this.Buffer = buffer;

            BlackBars = new BlackBarDetection(this);
        }

        #endregion

        #region Methods

        public void RequestUpdate() => IsUpdateRequested = true;

        public void SetUpdated()
        {
            IsUpdateRequested = false;
            BlackBars.InvalidateCache();

            Updated?.Invoke(this, new EventArgs());
        }

        public override int GetHashCode() => Id;
        public bool Equals(CaptureZone other) => Id == other.Id;
        public override bool Equals(object? obj) => obj is CaptureZone other && Equals(other);

        #endregion
    }
}