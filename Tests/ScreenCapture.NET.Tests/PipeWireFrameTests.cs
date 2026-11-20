extern alias Wayland;
using System.Diagnostics;
using System.Runtime.Versioning;
using Wayland::ScreenCapture.NET;
using static Wayland::ScreenCapture.NET.PipeWire;

namespace ScreenCapture.NET.Tests;

/// <summary>
/// Checks <see cref="PipeWireStream.TryReadFrame"/> with made-up buffers: a frame must never reach past the mapped memory the producer describes.
/// The pixels aren't read, so the buffers point to a made-up address.
/// </summary>
[TestClass]
[SupportedOSPlatform("linux")] // The Wayland assembly is marked linux-only, but the check is pure managed code that also runs on Windows.
public sealed unsafe class PipeWireFrameTests
{
    #region Constants

    // 10 pixels of 4 bytes per row.
    private const int WIDTH = 10;
    private const int HEIGHT = 5;
    private const int ROW = WIDTH * 4;

    private static readonly byte* DATA = (byte*)0x10000;

    #endregion

    #region Methods

    [TestMethod]
    [DataRow(200u, 0u, ROW, true, DisplayName = "Rows without padding fill the memory")]
    [DataRow(199u, 0u, ROW, false, DisplayName = "Rows without padding, one byte too little")]
    [DataRow(200u, 0u, 0, true, DisplayName = "No stride: the row size")]
    [DataRow(232u, 0u, 48, true, DisplayName = "Padded rows, the last one without padding")]
    [DataRow(231u, 0u, 48, false, DisplayName = "Padded rows, one byte too little")]
    [DataRow(200u, 0u, 48, false, DisplayName = "Stride times height over the memory")]
    [DataRow(208u, 8u, ROW, true, DisplayName = "Offset, fits")]
    [DataRow(207u, 8u, ROW, false, DisplayName = "Offset, one byte too little")]
    [DataRow(200u, 300u, ROW, false, DisplayName = "Offset past the end")]
    [DataRow(200u, uint.MaxValue, ROW, false, DisplayName = "Largest offset (no overflow)")]
    [DataRow(200u, 0u, ROW - 4, false, DisplayName = "Stride smaller than a row")]
    [DataRow(uint.MaxValue, 0u, int.MaxValue, false, DisplayName = "Largest stride (no overflow)")]
    public void FrameMustLieWithinTheMemory(uint maxSize, uint offset, int stride, bool expected)
    {
        Assert.AreEqual(expected, TryRead(new Layout { MaxSize = maxSize, Offset = offset, Stride = stride }, out PipeWireFrame frame));

        if (expected)
        {
            Assert.AreEqual((nint)(DATA + offset), frame.Data);
            Assert.AreEqual(stride > 0 ? stride : ROW, frame.Stride);
            Assert.AreEqual(WIDTH, frame.Width);
            Assert.AreEqual(HEIGHT, frame.Height);
        }
    }

    [TestMethod]
    [DataRow(nameof(Layout.NoSpaBuffer))]
    [DataRow(nameof(Layout.NoDatas))]
    [DataRow(nameof(Layout.NullDatas))]
    [DataRow(nameof(Layout.NoData))]
    [DataRow(nameof(Layout.NoChunk))]
    [DataRow(nameof(Layout.CorruptedChunk))]
    [DataRow(nameof(Layout.EmptyChunk))]
    [DataRow(nameof(Layout.CorruptedHeader))]
    [DataRow(nameof(Layout.NoWidth))]
    [DataRow(nameof(Layout.NoHeight))]
    public void BufferWithoutValidPixelsIsNoFrame(string defect)
    {
        Layout layout = defect switch
        {
            nameof(Layout.NoSpaBuffer) => new Layout { NoSpaBuffer = true },
            nameof(Layout.NoDatas) => new Layout { NoDatas = true },
            nameof(Layout.NullDatas) => new Layout { NullDatas = true },
            nameof(Layout.NoData) => new Layout { NoData = true },
            nameof(Layout.NoChunk) => new Layout { NoChunk = true },
            nameof(Layout.CorruptedChunk) => new Layout { CorruptedChunk = true },
            nameof(Layout.EmptyChunk) => new Layout { EmptyChunk = true },
            nameof(Layout.CorruptedHeader) => new Layout { CorruptedHeader = true },
            nameof(Layout.NoWidth) => new Layout { NoWidth = true },
            nameof(Layout.NoHeight) => new Layout { NoHeight = true },
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };

        Assert.IsTrue(TryRead(new Layout(), out _), "The buffer without the defect is a frame.");
        Assert.IsFalse(TryRead(layout, out PipeWireFrame frame));
        Assert.AreEqual(default, frame);
    }

    [TestMethod]
    public void FrameHasTheTimestampAndFormatOfTheBuffer()
    {
        Assert.IsTrue(TryRead(new Layout { Pts = 3_000_000_000 }, out PipeWireFrame frame));
        Assert.AreEqual(Stopwatch.Frequency * 3, frame.Timestamp);
        Assert.IsFalse(frame.HasAlpha);

        Assert.IsTrue(TryRead(new Layout { VideoFormat = SPA_VIDEO_FORMAT_BGRA }, out frame));
        Assert.IsTrue(frame.HasAlpha);

        // Metadata announced without its array: no timestamp, but the pixels are fine.
        Assert.IsTrue(TryRead(new Layout { Pts = 3_000_000_000, NullMetas = true }, out frame));
        Assert.AreEqual(0, frame.Timestamp);
    }

    private static bool TryRead(Layout layout, out PipeWireFrame frame)
    {
        SpaChunk chunk = new()
        {
            offset = layout.Offset,
            size = layout.EmptyChunk ? 0u : layout.MaxSize,
            stride = layout.Stride,
            flags = layout.CorruptedChunk ? SPA_CHUNK_FLAG_CORRUPTED : 0
        };

        SpaData data = new()
        {
            maxsize = layout.MaxSize,
            data = layout.NoData ? null : DATA,
            chunk = layout.NoChunk ? null : &chunk
        };

        SpaMetaHeader header = new() { pts = layout.Pts, flags = layout.CorruptedHeader ? SPA_META_HEADER_FLAG_CORRUPTED : 0u };
        SpaMeta meta = new() { type = SPA_META_Header, size = (uint)sizeof(SpaMetaHeader), data = &header };

        SpaBuffer spaBuffer = new()
        {
            n_datas = layout.NoDatas ? 0u : 1u,
            datas = layout.NullDatas ? null : &data,
            n_metas = 1,
            metas = layout.NullMetas ? null : &meta
        };

        PwBuffer buffer = new() { buffer = layout.NoSpaBuffer ? null : &spaBuffer };

        return PipeWireStream.TryReadFrame(&buffer, layout.NoWidth ? 0 : WIDTH, layout.NoHeight ? 0 : HEIGHT, layout.VideoFormat, out frame);
    }

    #endregion

    // A valid buffer of a 10x5 frame by default; each property breaks or changes one part.
    private sealed record Layout
    {
        public uint MaxSize { get; init; } = ROW * HEIGHT;
        public uint Offset { get; init; }
        public int Stride { get; init; } = ROW;
        public long Pts { get; init; }
        public uint VideoFormat { get; init; } = SPA_VIDEO_FORMAT_BGRx;

        public bool NoSpaBuffer { get; init; }
        public bool NoDatas { get; init; }
        public bool NullDatas { get; init; }
        public bool NoData { get; init; }
        public bool NoChunk { get; init; }
        public bool CorruptedChunk { get; init; }
        public bool EmptyChunk { get; init; }
        public bool CorruptedHeader { get; init; }
        public bool NullMetas { get; init; }
        public bool NoWidth { get; init; }
        public bool NoHeight { get; init; }
    }
}
