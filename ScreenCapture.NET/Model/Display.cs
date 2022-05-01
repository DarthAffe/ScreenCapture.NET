// ReSharper disable MemberCanBePrivate.Global

namespace ScreenCapture.NET;

/// <summary>
/// Represents a display connected to graphics-card.
/// </summary>
public readonly struct Display
{
    #region Properties & Fields

    /// <summary>
    /// Gets the index of the <see cref="Display"/>.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Gets the name of the <see cref="Display"/>.
    /// </summary>
    public string DeviceName { get; }

    /// <summary>
    /// Gets the with of the <see cref="Display"/>.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the height of the <see cref="Display"/>.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets the <see cref="GraphicsCard"/> this <see cref="Display"/> is connected to.
    /// </summary>
    public GraphicsCard GraphicsCard { get; }

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="Display"/> struct.
    /// </summary>
    /// <param name="index">The index of the <see cref="Display"/>.</param>
    /// <param name="deviceName">The name of the <see cref="Display"/>.</param>
    /// <param name="width">The with of the <see cref="Display"/>.</param>
    /// <param name="height">The height of the <see cref="Display"/>.</param>
    /// <param name="graphicsCard">The <see cref="GraphicsCard"/> this <see cref="Display"/> is connected to.</param>
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
        
    /// <summary>
    /// Determines whether this <see cref="Display"/> equals the given one.
    /// </summary>
    /// <param name="other">The <see cref="Display"/> to compare.</param>
    /// <returns><c>true</c> if the specified object is equal to the current object; otherwise, <c>false</c>.</returns>
    public bool Equals(Display other) => Index == other.Index;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Display other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Index;

    /// <summary>
    /// Determines whether two <see cref="Display"/> are equal.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><c>true</c> if the two specified displays are equal; otherwise, <c>false</c>.</returns>
    public static bool operator ==(Display left, Display right) => left.Equals(right);

    /// <summary>
    /// Determines whether two <see cref="Display"/> are not equal.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><c>true</c> if the two specified displays are not equal; otherwise, <c>false</c>.</returns>
    public static bool operator !=(Display left, Display right) => !(left == right);

    #endregion
}