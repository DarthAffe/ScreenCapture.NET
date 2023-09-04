namespace ScreenCapture.NET;

public readonly struct ColorFormat
{
    #region Instances

    public static readonly ColorFormat BGRA = new(1, 4);

    #endregion

    #region Properties & Fields

    public readonly int Id;
    public readonly int BytesPerPixel;

    #endregion

    #region Constructors

    private ColorFormat(int id, int bytesPerPixel)
    {
        this.Id = id;
        this.BytesPerPixel = bytesPerPixel;
    }

    #endregion

    #region Methods

    public bool Equals(ColorFormat other) => Id == other.Id;
    public override bool Equals(object? obj) => obj is ColorFormat other && Equals(other);

    public override int GetHashCode() => Id;

    #endregion

    #region Operators

    public static bool operator ==(ColorFormat left, ColorFormat right) => left.Equals(right);
    public static bool operator !=(ColorFormat left, ColorFormat right) => !(left == right);

    #endregion
}