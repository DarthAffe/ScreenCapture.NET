extern alias Wayland;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HPPH;
using static Wayland::ScreenCapture.NET.PipeWire;

namespace ScreenCapture.NET.Tests.Hardware;

/// <summary>
/// A video source node on the PipeWire daemon of the user (like the monitor a compositor shares), sending a frame of one color whenever <see cref="Send"/> is called.
/// The test is inconclusive if there's no PipeWire daemon. The session manager (WirePlumber) links the capture to it.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe partial class PipeWireVideoSource : IDisposable
{
    #region Constants

    private const string LIBRARY = "libpipewire-0.3.so.0";
    private const int BUFFER_COUNT = 8;

    #endregion

    #region Properties & Fields

    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly uint _videoFormat;

    private nint _loop;
    private nint _context;
    private nint _core;
    private nint _stream;
    private GCHandle _handle;
    private PwStreamEvents* _events;
    private void* _hook;

    // Only used under the lock of the PipeWire thread.
    private ColorBGRA _color;
    private long _pts;
    private bool _corrupted;

    /// <summary>
    /// Gets the id of the node.
    /// </summary>
    public uint NodeId { get; private set; }

    /// <summary>
    /// Gets the number of frames sent.
    /// </summary>
    public int SentFrames { get; private set; }

    #endregion

    #region Constructors

    private PipeWireVideoSource(int width, int height, int stride, uint videoFormat)
    {
        this._width = width;
        this._height = height;
        this._stride = stride;
        this._videoFormat = videoFormat;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Gets the socket of the PipeWire daemon of the user; the test is inconclusive if there's none.
    /// </summary>
    public static string GetDaemonSocket()
    {
        string? runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string socket = Path.Combine(runtimeDirectory ?? string.Empty, "pipewire-0");
        if (string.IsNullOrEmpty(runtimeDirectory) || !File.Exists(socket))
            Assert.Inconclusive("There's no PipeWire daemon ($XDG_RUNTIME_DIR/pipewire-0).");

        return socket;
    }

    /// <summary>
    /// Starts a source with frames of the given size and format (<see cref="SPA_VIDEO_FORMAT_BGRx"/> or <see cref="SPA_VIDEO_FORMAT_BGRA"/>).
    /// </summary>
    /// <param name="stride">The bytes per row; 0 for rows without padding.</param>
    public static PipeWireVideoSource Start(int width, int height, uint videoFormat = SPA_VIDEO_FORMAT_BGRx, int stride = 0)
    {
        _ = GetDaemonSocket();

        PipeWireVideoSource source = new(width, height, stride > 0 ? stride : width * 4, videoFormat);
        try
        {
            source.Connect();
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private void Connect()
    {
        pw_init(0, 0);
        _loop = pw_thread_loop_new("ScreenCapture.NET test source", 0);
        _context = pw_context_new(pw_thread_loop_get_loop(_loop), 0, 0);
        Assert.IsGreaterThanOrEqualTo(0, pw_thread_loop_start(_loop));

        pw_thread_loop_lock(_loop);
        try
        {
            _core = pw_context_connect(_context, 0, 0);
            Assert.AreNotEqual(0, _core, "Connecting to PipeWire failed.");

            _stream = pw_stream_new(_core, "ScreenCapture.NET test source", pw_properties_new_string("media.class=Video/Source"));
            _handle = GCHandle.Alloc(this);
            _events = (PwStreamEvents*)NativeMemory.AllocZeroed((nuint)sizeof(PwStreamEvents));
            _events->version = PW_VERSION_STREAM_EVENTS;
            _events->param_changed = &OnParamChanged;
            _events->process = &OnProcess;
            _hook = NativeMemory.AllocZeroed(SPA_HOOK_SIZE);
            pw_stream_add_listener(_stream, _hook, _events, GCHandle.ToIntPtr(_handle));

            // One fixed format: the capture gets exactly this one.
            byte[] format = Pod(SPA_TYPE_OBJECT_Format, SPA_PARAM_EnumFormat,
                                SPA_FORMAT_mediaType, 4, SPA_TYPE_Id, SPA_MEDIA_TYPE_video, 0,
                                SPA_FORMAT_mediaSubtype, 4, SPA_TYPE_Id, SPA_MEDIA_SUBTYPE_raw, 0,
                                SPA_FORMAT_VIDEO_format, 4, SPA_TYPE_Id, _videoFormat, 0,
                                SPA_FORMAT_VIDEO_size, 8, SPA_TYPE_Rectangle, (uint)_width, (uint)_height,
                                SPA_FORMAT_VIDEO_framerate, 8, SPA_TYPE_Fraction, 0, 1);
            fixed (byte* formatPointer = format)
            {
                byte* parameter = formatPointer;
                Assert.IsGreaterThanOrEqualTo(0, pw_stream_connect(_stream, PW_DIRECTION_OUTPUT, PW_ID_ANY, PW_STREAM_FLAG_DRIVER | PW_STREAM_FLAG_MAP_BUFFERS, &parameter, 1));
            }
        }
        finally
        {
            pw_thread_loop_unlock(_loop);
        }

        Assert.IsTrue(SpinWait.SpinUntil(() =>
        {
            pw_thread_loop_lock(_loop);
            try
            {
                NodeId = pw_stream_get_node_id(_stream);
                return NodeId != PW_ID_ANY;
            }
            finally
            {
                pw_thread_loop_unlock(_loop);
            }
        }, TimeSpan.FromSeconds(5)), "The source got no node.");
    }

    /// <summary>
    /// Sends a frame filled with the color (as bytes B, G, R, A or x).
    /// </summary>
    /// <param name="pts">The presentation time in the header of the frame (nanoseconds of CLOCK_MONOTONIC).</param>
    /// <param name="corrupted">Whether the chunk of the frame is marked as corrupted.</param>
    public void Send(ColorBGRA color, long pts = 0, bool corrupted = false)
    {
        pw_thread_loop_lock(_loop);
        try
        {
            _color = color;
            _pts = pts;
            _corrupted = corrupted;
            _ = pw_stream_trigger_process(_stream);
        }
        finally
        {
            pw_thread_loop_unlock(_loop);
        }
    }

    // Object PODs from plain 32-bit words: every property is key, flags (0), then the value (size, type and the value padded to 8 bytes).
    private static byte[] Pod(uint objectType, uint id, params uint[] properties)
    {
        List<uint> words = [0, SPA_TYPE_Object, objectType, id];
        for (int i = 0; i < properties.Length; i += 5)
            words.AddRange([properties[i], 0, properties[i + 1], properties[i + 2], properties[i + 3], properties[i + 4]]);

        words[0] = (uint)((words.Count - 2) * 4);
        return MemoryMarshal.AsBytes(words.ToArray().AsSpan()).ToArray();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnParamChanged(nint data, uint id, byte* param)
    {
        if ((id != SPA_PARAM_Format) || (param == null)) return;

        PipeWireVideoSource source = (PipeWireVideoSource)GCHandle.FromIntPtr(data).Target!;

        // The source sets the size of its buffers (PipeWire allocates them); both ends want the header metadata.
        byte[] buffers = Pod(SPA_TYPE_OBJECT_ParamBuffers, SPA_PARAM_Buffers,
                             SPA_PARAM_BUFFERS_buffers, 4, SPA_TYPE_Int, BUFFER_COUNT, 0,
                             SPA_PARAM_BUFFERS_blocks, 4, SPA_TYPE_Int, 1, 0,
                             SPA_PARAM_BUFFERS_size, 4, SPA_TYPE_Int, (uint)(source._stride * source._height), 0,
                             SPA_PARAM_BUFFERS_stride, 4, SPA_TYPE_Int, (uint)source._stride, 0);
        byte[] meta = Pod(SPA_TYPE_OBJECT_ParamMeta, SPA_PARAM_Meta,
                          SPA_PARAM_META_type, 4, SPA_TYPE_Id, SPA_META_Header, 0,
                          SPA_PARAM_META_size, 4, SPA_TYPE_Int, (uint)sizeof(SpaMetaHeader), 0);
        fixed (byte* buffersPointer = buffers)
        fixed (byte* metaPointer = meta)
        {
            byte** parameters = stackalloc byte*[2] { buffersPointer, metaPointer };
            _ = pw_stream_update_params(source._stream, parameters, 2);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnProcess(nint data)
    {
        PipeWireVideoSource source = (PipeWireVideoSource)GCHandle.FromIntPtr(data).Target!;

        PwBuffer* buffer = pw_stream_dequeue_buffer(source._stream);
        if (buffer == null) return;

        SpaData* spaData = buffer->buffer->datas;
        if ((spaData->data != null) && (spaData->maxsize >= (uint)(source._stride * source._height)))
        {
            // The padding of the rows gets another color, so a capture that reads it shows.
            new Span<byte>(spaData->data, source._stride * source._height).Fill(0x5A);
            for (int y = 0; y < source._height; y++)
                new Span<ColorBGRA>(spaData->data + (y * source._stride), source._width).Fill(source._color);

            spaData->chunk->offset = 0;
            spaData->chunk->size = (uint)(source._stride * source._height);
            spaData->chunk->stride = source._stride;
            spaData->chunk->flags = source._corrupted ? SPA_CHUNK_FLAG_CORRUPTED : 0;

            for (uint i = 0; i < buffer->buffer->n_metas; i++)
                if (buffer->buffer->metas[i].type == SPA_META_Header)
                    ((SpaMetaHeader*)buffer->buffer->metas[i].data)->pts = source._pts;

            source.SentFrames++;
        }

        _ = pw_stream_queue_buffer(source._stream, buffer);
    }

    public void Dispose()
    {
        if (_loop != 0)
        {
            pw_thread_loop_lock(_loop);
            if (_stream != 0) pw_stream_destroy(_stream);
            if (_core != 0) _ = pw_core_disconnect(_core);
            pw_thread_loop_unlock(_loop);
            pw_thread_loop_stop(_loop);
        }

        if (_context != 0) pw_context_destroy(_context);
        if (_loop != 0) pw_thread_loop_destroy(_loop);
        if (_events != null) NativeMemory.Free(_events);
        if (_hook != null) NativeMemory.Free(_hook);
        if (_handle.IsAllocated) _handle.Free();

        _stream = 0;
        _core = 0;
        _context = 0;
        _loop = 0;
        _events = null;
        _hook = null;
    }

    // The calls only a source needs (the capture connects on the socket of the portal and never drives the graph).
    [LibraryImport(LIBRARY)]
    private static partial nint pw_context_connect(nint context, nint props, nuint userDataSize);

    [LibraryImport(LIBRARY)]
    private static partial uint pw_stream_get_node_id(nint stream);

    [LibraryImport(LIBRARY)]
    private static partial int pw_stream_trigger_process(nint stream);

    #endregion
}
