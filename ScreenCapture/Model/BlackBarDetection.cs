// ReSharper disable MemberCanBePrivate.Global
using System;

namespace ScreenCapture
{
    /// <summary>
    /// Represents the configuration for the detection and removal of black bars around the screen image.
    /// </summary>
    public sealed class BlackBarDetection
    {
        #region Properties & Fields

        private readonly CaptureZone _captureZone;

        private int? _top;
        /// <summary>
        /// Gets the size of the detected black bar at the top of the image.
        /// </summary>
        public int Top => _top ??= CalculateTop();

        private int? _bottom;
        /// <summary>
        /// Gets the size of the detected black bar at the bottom of the image.
        /// </summary>
        public int Bottom => _bottom ??= CalculateBottom();

        private int? _left;
        /// <summary>
        /// Gets the size of the detected black bar at the left of the image.
        /// </summary>
        public int Left => _left ??= CalculateLeft();

        private int? _right;
        /// <summary>
        /// Gets the size of the detected black bar at the right of the image.
        /// </summary>
        public int Right => _right ??= CalculateRight();

        private int _theshold = 0;
        /// <summary>
        /// Gets or sets the threshold of "blackness" used to detect black bars. (e. g. Threshold 5 will consider a pixel of color [5,5,5] as black.) (default 0)
        /// </summary>
        public int Threshold
        {
            get => _theshold;
            set
            {
                _theshold = value;
                InvalidateCache();
            }
        }

        #endregion

        #region Constructors

        internal BlackBarDetection(CaptureZone captureZone)
        {
            this._captureZone = captureZone;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Invalidates the cached values and recalculates <see cref="Top"/>, <see cref="Bottom"/>, <see cref="Left"/> and <see cref="Right"/>.
        /// </summary>
        public void InvalidateCache()
        {
            _top = null;
            _bottom = null;
            _left = null;
            _right = null;
        }

        private int CalculateTop()
        {
            int threshold = Threshold;
            int stride = _captureZone.BufferWidth * 4;
            int bytesPerRow = _captureZone.Width * 4;
            for (int row = 0; row < _captureZone.Height; row++)
            {
                Span<byte> data = new(_captureZone.Buffer, row * stride, bytesPerRow);
                for (int i = 0; i < data.Length; i += 4)
                    if ((data[i] > threshold) || (data[i + 1] > threshold) || (data[i + 2] > threshold))
                        return row;
            }

            return 0;
        }

        private int CalculateBottom()
        {
            int threshold = Threshold;
            int stride = _captureZone.BufferWidth * 4;
            int bytesPerRow = _captureZone.Width * 4;
            for (int row = _captureZone.Height; row >= 0; row--)
            {
                Span<byte> data = new(_captureZone.Buffer, row * stride, bytesPerRow);
                for (int i = 0; i < data.Length; i += 4)
                    if ((data[i] > threshold) || (data[i + 1] > threshold) || (data[i + 2] > threshold))
                        return _captureZone.Height - row;
            }

            return 0;
        }

        private int CalculateLeft()
        {
            int threshold = Threshold;
            int stride = _captureZone.BufferWidth * 4;
            byte[] buffer = _captureZone.Buffer;
            for (int column = 0; column < _captureZone.Width; column++)
                for (int row = 0; row < _captureZone.Height; row++)
                {
                    int offset = (stride * row) + (column * 4);
                    if ((buffer[offset] > threshold) || (buffer[offset + 1] > threshold) || (buffer[offset + 2] > threshold)) return column;
                }

            return 0;
        }

        private int CalculateRight()
        {
            int threshold = Threshold;
            int stride = _captureZone.BufferWidth * 4;
            byte[] buffer = _captureZone.Buffer;
            for (int column = _captureZone.Width; column >= 0; column--)
                for (int row = 0; row < _captureZone.Height; row++)
                {
                    int offset = (stride * row) + (column * 4);
                    if ((buffer[offset] > threshold) || (buffer[offset + 1] > threshold) || (buffer[offset + 2] > threshold)) return _captureZone.Width - column;
                }

            return 0;
        }

        #endregion
    }
}
