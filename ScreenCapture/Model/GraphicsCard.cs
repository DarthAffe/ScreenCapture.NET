namespace ScreenCapture
{
    public readonly struct GraphicsCard
    {
        #region Properties & Fields

        public int Index { get; }
        public string Name { get; }
        public int VendorId { get; }
        public int DeviceId { get; }

        #endregion

        #region Constructors

        public GraphicsCard(int index, string name, int vendorId, int deviceId)
        {
            this.Index = index;
            this.Name = name;
            this.VendorId = vendorId;
            this.DeviceId = deviceId;
        }

        #endregion

        #region Methods

        public bool Equals(GraphicsCard other) => Index == other.Index;
        public override bool Equals(object? obj) => obj is GraphicsCard other && Equals(other);
        public override int GetHashCode() => Index;

        #endregion
    }
}
