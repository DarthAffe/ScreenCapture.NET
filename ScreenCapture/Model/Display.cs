namespace ScreenCapture
{
    public readonly struct Display
    {
        #region Properties & Fields

        public int Index { get; }
        public string DeviceName { get; }

        public int Width { get; }
        public int Height { get; }

        public GraphicsCard GraphicsCard { get; }

        #endregion

        #region Constructors

        public Display(int index, string deviceName, int width, int height, GraphicsCard graphicsCard)
        {
            this.Index = index;
            this.DeviceName = deviceName;
            this.Width = width;
            this.Height = height;
            this.GraphicsCard = graphicsCard;
        }

        #endregion

        #region Methods

        public bool Equals(Display other) => Index == other.Index;
        public override bool Equals(object? obj) => obj is Display other && Equals(other);
        public override int GetHashCode() => Index;

        #endregion
    }
}
