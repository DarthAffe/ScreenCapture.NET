using System;
using System.Collections.Generic;
using HPPH;
using Tmds.DBus.Protocol;

namespace ScreenCapture.NET;

/// <summary>
/// Maps the streams of a screen cast session to displays.
/// </summary>
internal static class WaylandDisplays
{
    #region Constants

    /// <summary>
    /// The id of the only graphics card: the portal doesn't tell which card shows a stream.
    /// </summary>
    public const string GRAPHICS_CARD_ID = "xdg-desktop-portal";

    #endregion

    #region Methods

    /// <summary>
    /// Gets the graphics card, named after the desktop ($XDG_CURRENT_DESKTOP, e.g. <c>KDE</c> or <c>ubuntu:GNOME</c>).
    /// </summary>
    public static GraphicsCard GetGraphicsCard()
    {
        string? desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        return new GraphicsCard(GRAPHICS_CARD_ID, 0, string.IsNullOrEmpty(desktop) ? GRAPHICS_CARD_ID : desktop, 0, 0);
    }

    /// <summary>
    /// Gets a display per stream, in the order of the portal.
    /// </summary>
    public static List<Display> GetDisplays(IReadOnlyList<PortalStream> streams, GraphicsCard graphicsCard)
    {
        List<Display> displays = new(streams.Count);
        for (int i = 0; i < streams.Count; i++)
        {
            PortalStream stream = streams[i];
            displays.Add(new Display(stream.Id == null ? $"stream:{i}" : $"stream:{stream.Id}", i, $"Stream {i}", string.Empty,
                                     stream.X, stream.Y, stream.Width, stream.Height, ImageRotation.None, graphicsCard));
        }

        return displays;
    }

    #endregion
}

/// <summary>
/// A stream of a screen cast session: a PipeWire node with what the portal tells about it.
/// </summary>
/// <param name="NodeId">The PipeWire node of the stream.</param>
/// <param name="Id">The id the portal gives the stream (version 4 and later; optional, e.g. KDE doesn't send it); unique in the session and kept by restored sessions.</param>
/// <param name="X">The x-coordinate of the monitor in the compositor's space; 0 if the portal doesn't send it.</param>
/// <param name="Y">The y-coordinate of the monitor in the compositor's space; 0 if the portal doesn't send it.</param>
/// <param name="Width">The width of the stream; 0 if the portal doesn't send it.</param>
/// <param name="Height">The height of the stream; 0 if the portal doesn't send it.</param>
internal readonly record struct PortalStream(uint NodeId, string? Id, int X, int Y, int Width, int Height)
{
    /// <summary>
    /// Reads the <c>streams</c> (<c>a(ua{sv})</c>) of the results of <c>Start</c>. Properties with another type than documented are ignored.
    /// </summary>
    /// <exception cref="InvalidOperationException">The results have no streams.</exception>
    public static List<PortalStream> ReadStreams(Dictionary<string, VariantValue> results)
    {
        if (!results.TryGetValue("streams", out VariantValue streams) || (streams.Type != VariantValueType.Array))
            throw new InvalidOperationException("The screen cast portal started the session without streams.");

        List<PortalStream> result = new(streams.Count);
        for (int i = 0; i < streams.Count; i++)
        {
            VariantValue stream = streams.GetItem(i);
            VariantValue properties = stream.GetItem(1);

            string? id = null;
            (int x, int y) = (0, 0);
            (int width, int height) = (0, 0);
            for (int j = 0; j < properties.Count; j++)
            {
                KeyValuePair<VariantValue, VariantValue> property = properties.GetDictionaryEntry(j);
                VariantValue value = Unwrap(property.Value);
                switch (property.Key.GetString())
                {
                    case "id" when value.Type == VariantValueType.String:
                        id = value.GetString();
                        break;

                    case "position" when IsPair(value):
                        (x, y) = (value.GetItem(0).GetInt32(), value.GetItem(1).GetInt32());
                        break;

                    case "size" when IsPair(value):
                        (width, height) = (value.GetItem(0).GetInt32(), value.GetItem(1).GetInt32());
                        break;
                }
            }

            result.Add(new PortalStream(stream.GetItem(0).GetUInt32(), id, x, y, width, height));
        }

        return result;
    }

    private static VariantValue Unwrap(VariantValue value) => value.Type == VariantValueType.Variant ? value.GetVariantValue() : value;

    // (ii)
    private static bool IsPair(VariantValue value)
        => (value.Type == VariantValueType.Struct) && (value.Count == 2)
        && (value.GetItem(0).Type == VariantValueType.Int32) && (value.GetItem(1).Type == VariantValueType.Int32);
}
