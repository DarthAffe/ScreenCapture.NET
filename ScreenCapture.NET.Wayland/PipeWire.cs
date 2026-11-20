using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]

namespace ScreenCapture.NET;

/// <summary>
/// The parts of libpipewire (and the SPA types it uses) the capture needs. Layouts are the ones of 64-bit Linux (pinned by tests).
/// https://docs.pipewire.org/page_streams.html
/// </summary>
// ReSharper disable InconsistentNaming
internal static unsafe partial class PipeWire
{
    #region Constants

    private const string LIBRARY = "libpipewire-0.3.so.0";

    public const uint PW_VERSION_STREAM_EVENTS = 2;
    public const uint PW_ID_ANY = 0xFFFFFFFF;

    public const int PW_DIRECTION_INPUT = 0;
    public const int PW_DIRECTION_OUTPUT = 1;

    public const int PW_STREAM_FLAG_AUTOCONNECT = 1 << 0;
    public const int PW_STREAM_FLAG_MAP_BUFFERS = 1 << 2;
    public const int PW_STREAM_FLAG_DRIVER = 1 << 3;

    public const int PW_STREAM_STATE_ERROR = -1;
    public const int PW_STREAM_STATE_UNCONNECTED = 0;
    public const int PW_STREAM_STATE_CONNECTING = 1;
    public const int PW_STREAM_STATE_PAUSED = 2;
    public const int PW_STREAM_STATE_STREAMING = 3;

    public const uint SPA_TYPE_Id = 3;
    public const uint SPA_TYPE_Int = 4;
    public const uint SPA_TYPE_Rectangle = 10;
    public const uint SPA_TYPE_Fraction = 11;
    public const uint SPA_TYPE_Object = 15;
    public const uint SPA_TYPE_Choice = 19;

    public const uint SPA_TYPE_OBJECT_Format = 0x40003;
    public const uint SPA_TYPE_OBJECT_ParamBuffers = 0x40004;
    public const uint SPA_TYPE_OBJECT_ParamMeta = 0x40005;

    public const uint SPA_PARAM_EnumFormat = 3;
    public const uint SPA_PARAM_Format = 4;
    public const uint SPA_PARAM_Buffers = 5;
    public const uint SPA_PARAM_Meta = 6;

    public const uint SPA_CHOICE_None = 0;
    public const uint SPA_CHOICE_Range = 1;
    public const uint SPA_CHOICE_Enum = 3;
    public const uint SPA_CHOICE_Flags = 4;

    public const uint SPA_FORMAT_mediaType = 1;
    public const uint SPA_FORMAT_mediaSubtype = 2;
    public const uint SPA_FORMAT_VIDEO_format = 0x20001;
    public const uint SPA_FORMAT_VIDEO_size = 0x20003;
    public const uint SPA_FORMAT_VIDEO_framerate = 0x20004;

    public const uint SPA_MEDIA_TYPE_video = 2;
    public const uint SPA_MEDIA_SUBTYPE_raw = 1;

    public const uint SPA_PARAM_BUFFERS_buffers = 1;
    public const uint SPA_PARAM_BUFFERS_blocks = 2;
    public const uint SPA_PARAM_BUFFERS_size = 3;
    public const uint SPA_PARAM_BUFFERS_stride = 4;
    public const uint SPA_PARAM_BUFFERS_dataType = 6;

    public const uint SPA_PARAM_META_type = 1;
    public const uint SPA_PARAM_META_size = 2;

    public const uint SPA_META_Header = 1;

    public const uint SPA_DATA_MemPtr = 1;
    public const uint SPA_DATA_MemFd = 2;

    public const uint SPA_VIDEO_FORMAT_BGRx = 8;
    public const uint SPA_VIDEO_FORMAT_BGRA = 12;

    public const int SPA_CHUNK_FLAG_CORRUPTED = 1 << 0;
    public const uint SPA_META_HEADER_FLAG_CORRUPTED = 1 << 1;

    public const int SPA_HOOK_SIZE = 48;

    #endregion

    #region Structs

    [StructLayout(LayoutKind.Sequential)]
    public struct PwStreamEvents
    {
        public uint version;
        public nint destroy;
        public delegate* unmanaged[Cdecl]<nint, int, int, byte*, void> state_changed;
        public nint control_info;
        public nint io_changed;
        public delegate* unmanaged[Cdecl]<nint, uint, byte*, void> param_changed;
        public nint add_buffer;
        public delegate* unmanaged[Cdecl]<nint, PwBuffer*, void> remove_buffer;
        public delegate* unmanaged[Cdecl]<nint, void> process;
        public nint drained;
        public nint command;
        public nint trigger_done;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PwBuffer
    {
        public SpaBuffer* buffer;
        public nint user_data;
        public ulong size;
        public ulong requested;
        public ulong time;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpaBuffer
    {
        public uint n_metas;
        public uint n_datas;
        public SpaMeta* metas;
        public SpaData* datas;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpaData
    {
        public uint type;
        public uint flags;
        public long fd;
        public uint mapoffset;
        public uint maxsize;
        public byte* data;
        public SpaChunk* chunk;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpaChunk
    {
        public uint offset;
        public uint size;
        public int stride;
        public int flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpaMeta
    {
        public uint type;
        public uint size;
        public void* data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpaMetaHeader
    {
        public uint flags;
        public uint offset;
        public long pts;
        public long dts_offset;
        public ulong seq;
    }

    #endregion

    #region Methods

    [LibraryImport(LIBRARY)]
    public static partial void pw_init(nint argc, nint argv);

    [LibraryImport(LIBRARY, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint pw_thread_loop_new(string name, nint props);

    [LibraryImport(LIBRARY)]
    public static partial nint pw_thread_loop_get_loop(nint loop);

    [LibraryImport(LIBRARY)]
    public static partial int pw_thread_loop_start(nint loop);

    [LibraryImport(LIBRARY)]
    public static partial void pw_thread_loop_stop(nint loop);

    [LibraryImport(LIBRARY)]
    public static partial void pw_thread_loop_lock(nint loop);

    [LibraryImport(LIBRARY)]
    public static partial void pw_thread_loop_unlock(nint loop);

    [LibraryImport(LIBRARY)]
    public static partial void pw_thread_loop_destroy(nint loop);

    [LibraryImport(LIBRARY)]
    public static partial nint pw_context_new(nint loop, nint props, nuint userDataSize);

    [LibraryImport(LIBRARY)]
    public static partial void pw_context_destroy(nint context);

    /// <summary>
    /// Connects to the PipeWire daemon on a connected socket, which is closed on disconnect or error (also if this fails).
    /// </summary>
    [LibraryImport(LIBRARY)]
    public static partial nint pw_context_connect_fd(nint context, int fd, nint props, nuint userDataSize);

    [LibraryImport(LIBRARY)]
    public static partial int pw_core_disconnect(nint core);

    [LibraryImport(LIBRARY, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint pw_properties_new_string(string text);

    [LibraryImport(LIBRARY, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint pw_stream_new(nint core, string name, nint props);

    [LibraryImport(LIBRARY)]
    public static partial void pw_stream_add_listener(nint stream, void* hook, PwStreamEvents* events, nint data);

    [LibraryImport(LIBRARY)]
    public static partial int pw_stream_connect(nint stream, int direction, uint targetId, int flags, byte** parameters, uint count);

    [LibraryImport(LIBRARY)]
    public static partial int pw_stream_update_params(nint stream, byte** parameters, uint count);

    [LibraryImport(LIBRARY)]
    public static partial PwBuffer* pw_stream_dequeue_buffer(nint stream);

    [LibraryImport(LIBRARY)]
    public static partial int pw_stream_queue_buffer(nint stream, PwBuffer* buffer);

    [LibraryImport(LIBRARY)]
    public static partial void pw_stream_destroy(nint stream);

    #endregion
}
