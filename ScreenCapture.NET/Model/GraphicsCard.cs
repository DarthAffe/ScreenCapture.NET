// ReSharper disable MemberCanBePrivate.Global

namespace ScreenCapture.NET;

/// <summary>
/// Represents a graphics-card.
/// </summary>
public readonly struct GraphicsCard
{
    #region Properties & Fields

    /// <summary>
    /// Gets the index of the <see cref="GraphicsCard"/>.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Gets the name of the <see cref="GraphicsCard"/>.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the vendor-id of the <see cref="GraphicsCard"/>.
    /// </summary>
    public int VendorId { get; }

    /// <summary>
    /// Gets the device-id of the <see cref="GraphicsCard"/>.
    /// </summary>
    public int DeviceId { get; }

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GraphicsCard"/> struct.
    /// </summary>
    /// <param name="index">The index of the <see cref="GraphicsCard"/>.</param>
    /// <param name="name">The name of the <see cref="GraphicsCard"/>.</param>
    /// <param name="vendorId">The vendor-id of the <see cref="GraphicsCard"/>.</param>
    /// <param name="deviceId">The device-id of the <see cref="GraphicsCard"/>.</param>
    public GraphicsCard(int index, string name, int vendorId, int deviceId)
    {
        this.Index = index;
        this.Name = name;
        this.VendorId = vendorId;
        this.DeviceId = deviceId;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Determines whether this <see cref="Display"/> equals the given one.
    /// </summary>
    /// <param name="other">The <see cref="Display"/> to compare.</param>
    /// <returns><c>true</c> if the specified object is equal to the current object; otherwise, <c>false</c>.</returns>
    public bool Equals(GraphicsCard other) => Index == other.Index;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GraphicsCard other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Index;

    /// <summary>
    /// Determines whether two <see cref="GraphicsCard"/> are equal.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><c>true</c> if the two specified graphics-cards are equal; otherwise, <c>false</c>.</returns>
    public static bool operator ==(GraphicsCard left, GraphicsCard right) => left.Equals(right);

    /// <summary>
    /// Determines whether two <see cref="GraphicsCard"/> are not equal.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><c>true</c> if the two specified graphics-cards are not equal; otherwise, <c>false</c>.</returns>
    public static bool operator !=(GraphicsCard left, GraphicsCard right) => !(left == right);

    #endregion
}