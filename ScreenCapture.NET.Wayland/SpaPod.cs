using System;
using System.Buffers.Binary;
using static ScreenCapture.NET.PipeWire;

namespace ScreenCapture.NET;

/// <summary>
/// Builds and reads the SPA PODs (plain old data) the stream negotiates with: every value has a header (size and type) and is padded to 8 bytes.
/// https://docs.pipewire.org/page_spa_pod.html
/// </summary>
internal static class SpaPod
{
    #region Constants

    // Big enough for every POD built here.
    private const int MAX_SIZE = 512;

    // The largest size offered: the compositor picks the size of the monitor.
    private const int MAX_DIMENSION = 16384;

    // The capture holds two buffers between captures (the current frame and the newest one, see PipeWireStream): with a third the compositor can always send a newer frame.
    // Fewer are accepted, so a compositor that offers at most two still streams (with frames that can be one capture old); any more are fine.
    // A compositor that drives the stream (all of them do) takes this default if its range allows it; otherwise its own default counts.
    private const int PREFERRED_BUFFERS = 3;
    private const int MIN_BUFFERS = 2;

    #endregion

    #region Methods

    /// <summary>
    /// Builds the formats the capture accepts: raw video in BGRx or BGRA (no modifiers, so the buffers are in memory, not DMA-BUFs), of any size and frame rate.
    /// </summary>
    /// <param name="width">The preferred width (the default of the range).</param>
    /// <param name="height">The preferred height (the default of the range).</param>
    public static byte[] BuildEnumFormat(int width, int height)
    {
        Builder builder = new(stackalloc byte[MAX_SIZE]);
        int format = builder.StartObject(SPA_TYPE_OBJECT_Format, SPA_PARAM_EnumFormat);
        builder.Property(SPA_FORMAT_mediaType);
        builder.Id(SPA_MEDIA_TYPE_video);
        builder.Property(SPA_FORMAT_mediaSubtype);
        builder.Id(SPA_MEDIA_SUBTYPE_raw);
        builder.Property(SPA_FORMAT_VIDEO_format);
        builder.Choice(SPA_CHOICE_Enum, SPA_TYPE_Id, [SPA_VIDEO_FORMAT_BGRx, SPA_VIDEO_FORMAT_BGRx, SPA_VIDEO_FORMAT_BGRA]);
        builder.Property(SPA_FORMAT_VIDEO_size);
        builder.Choice(SPA_CHOICE_Range, SPA_TYPE_Rectangle, [(uint)Math.Max(1, width), (uint)Math.Max(1, height), 1, 1, MAX_DIMENSION, MAX_DIMENSION]);
        builder.Property(SPA_FORMAT_VIDEO_framerate);
        builder.Choice(SPA_CHOICE_Range, SPA_TYPE_Fraction, [0, 1, 0, 1, 1000, 1]);
        builder.EndObject(format);

        return builder.ToArray();
    }

    /// <summary>
    /// Builds the buffers the capture accepts: preferably three (see <see cref="PREFERRED_BUFFERS"/>), in memory, mapped by PipeWire (no DMA-BUFs).
    /// </summary>
    public static byte[] BuildBuffers()
    {
        Builder builder = new(stackalloc byte[MAX_SIZE]);
        int buffers = builder.StartObject(SPA_TYPE_OBJECT_ParamBuffers, SPA_PARAM_Buffers);
        builder.Property(SPA_PARAM_BUFFERS_buffers);
        builder.Choice(SPA_CHOICE_Range, SPA_TYPE_Int, [PREFERRED_BUFFERS, MIN_BUFFERS, int.MaxValue]);
        builder.Property(SPA_PARAM_BUFFERS_dataType);
        builder.Choice(SPA_CHOICE_Flags, SPA_TYPE_Int, [(1u << (int)SPA_DATA_MemPtr) | (1u << (int)SPA_DATA_MemFd)]);
        builder.EndObject(buffers);

        return builder.ToArray();
    }

    /// <summary>
    /// Builds the request for the header metadata of the buffers (with the time the compositor produced the frame).
    /// </summary>
    public static unsafe byte[] BuildMetaHeader()
    {
        Builder builder = new(stackalloc byte[MAX_SIZE]);
        int meta = builder.StartObject(SPA_TYPE_OBJECT_ParamMeta, SPA_PARAM_Meta);
        builder.Property(SPA_PARAM_META_type);
        builder.Id(SPA_META_Header);
        builder.Property(SPA_PARAM_META_size);
        builder.Int(sizeof(SpaMetaHeader));
        builder.EndObject(meta);

        return builder.ToArray();
    }

    /// <summary>
    /// Reads the negotiated format (<c>SPA_PARAM_Format</c>): raw video with its pixel format and size.
    /// A property that is a choice counts with its first value (the default).
    /// </summary>
    /// <param name="pod">The POD, at least as long as its header says.</param>
    /// <param name="videoFormat">The pixel format (<c>SPA_VIDEO_FORMAT_*</c>).</param>
    /// <param name="width">The width of the frames.</param>
    /// <param name="height">The height of the frames.</param>
    /// <returns><c>false</c> if it isn't a format object, not raw video, or lacks the pixel format or the size.</returns>
    public static bool TryReadVideoFormat(ReadOnlySpan<byte> pod, out uint videoFormat, out int width, out int height)
    {
        videoFormat = 0;
        width = 0;
        height = 0;

        if (pod.Length < 16) return false;

        int size = (int)ReadUInt(pod, 0);
        if ((ReadUInt(pod, 4) != SPA_TYPE_Object) || (size < 8) || (pod.Length < (8 + size)) || (ReadUInt(pod, 8) != SPA_TYPE_OBJECT_Format)) return false;

        bool isVideo = false;
        bool isRaw = false;
        bool hasFormat = false;
        bool hasSize = false;

        ReadOnlySpan<byte> properties = pod[16..(8 + size)];
        while (properties.Length >= 16)
        {
            uint key = ReadUInt(properties, 0);
            int valueSize = (int)ReadUInt(properties, 8);
            uint valueType = ReadUInt(properties, 12);
            if ((valueSize < 0) || (properties.Length < (16 + valueSize))) return false;

            ReadOnlySpan<byte> value = properties.Slice(16, valueSize);

            // A choice: its body (kind, flags), the header of its values (size, type), then the values (the first is the default).
            if (valueType == SPA_TYPE_Choice)
            {
                if (value.Length < 16) return false;

                int childSize = (int)ReadUInt(value, 8);
                valueType = ReadUInt(value, 12);
                if ((childSize < 0) || (value.Length < (16 + childSize))) return false;

                value = value.Slice(16, childSize);
            }

            switch (key)
            {
                case SPA_FORMAT_mediaType when (valueType == SPA_TYPE_Id) && (value.Length >= 4):
                    isVideo = ReadUInt(value, 0) == SPA_MEDIA_TYPE_video;
                    break;

                case SPA_FORMAT_mediaSubtype when (valueType == SPA_TYPE_Id) && (value.Length >= 4):
                    isRaw = ReadUInt(value, 0) == SPA_MEDIA_SUBTYPE_raw;
                    break;

                case SPA_FORMAT_VIDEO_format when (valueType == SPA_TYPE_Id) && (value.Length >= 4):
                    videoFormat = ReadUInt(value, 0);
                    hasFormat = true;
                    break;

                case SPA_FORMAT_VIDEO_size when (valueType == SPA_TYPE_Rectangle) && (value.Length >= 8):
                    width = (int)ReadUInt(value, 0);
                    height = (int)ReadUInt(value, 4);
                    hasSize = true;
                    break;
            }

            int next = 16 + Pad(valueSize);
            if (next >= properties.Length) break;

            properties = properties[next..];
        }

        return isVideo && isRaw && hasFormat && hasSize;
    }

    private static uint ReadUInt(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    private static int Pad(int size) => (size + 7) & ~7;

    #endregion

    // Writes into a fixed buffer; nested sizes are patched when an object ends.
    private ref struct Builder(Span<byte> buffer)
    {
        private readonly Span<byte> _buffer = buffer;
        private int _length;

        /// <returns>Where the object starts, for <see cref="EndObject"/>.</returns>
        public int StartObject(uint objectType, uint id)
        {
            int start = _length;
            WriteUInt(0); // size, patched by EndObject
            WriteUInt(SPA_TYPE_Object);
            WriteUInt(objectType);
            WriteUInt(id);
            return start;
        }

        public readonly void EndObject(int start) => BinaryPrimitives.WriteUInt32LittleEndian(_buffer[start..], (uint)(_length - start - 8));

        public void Property(uint key)
        {
            WriteUInt(key);
            WriteUInt(0); // flags
        }

        public void Id(uint value)
        {
            WriteUInt(4);
            WriteUInt(SPA_TYPE_Id);
            WriteUInt(value);
            PadTo8();
        }

        public void Int(int value)
        {
            WriteUInt(4);
            WriteUInt(SPA_TYPE_Int);
            WriteUInt((uint)value);
            PadTo8();
        }

        // Every value of the types used here is a multiple of 4 bytes: Id and Int one, Rectangle and Fraction two.
        public void Choice(uint kind, uint childType, ReadOnlySpan<uint> values)
        {
            uint childSize = childType is SPA_TYPE_Rectangle or SPA_TYPE_Fraction ? 8u : 4u;

            WriteUInt((uint)(16 + (values.Length * 4)));
            WriteUInt(SPA_TYPE_Choice);
            WriteUInt(kind);
            WriteUInt(0); // flags
            WriteUInt(childSize);
            WriteUInt(childType);
            foreach (uint value in values)
                WriteUInt(value);

            PadTo8();
        }

        public readonly byte[] ToArray() => _buffer[.._length].ToArray();

        private void WriteUInt(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer[_length..], value);
            _length += 4;
        }

        private void PadTo8()
        {
            while ((_length % 8) != 0)
                _buffer[_length++] = 0;
        }
    }
}
