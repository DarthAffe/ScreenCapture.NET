using System;

namespace ScreenCapture
{
    public sealed class BlackBarDetection
    {
        #region Properties & Fields

        private readonly CaptureZone _captureZone;

        private int? _top;
        public int Top => _top ??= CalculateTop();

        private int? _bottom;
        public int Bottom => _bottom ??= CalculateBottom();

        private int? _left;
        public int Left => _left ??= CalculateLeft();

        private int? _right;
        public int Right => _right ??= CalculateRight();

        private int _theshold = 0;
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

        public BlackBarDetection(CaptureZone captureZone)
        {
            this._captureZone = captureZone;
        }

        #endregion

        #region Methods

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
