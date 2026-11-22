using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using static ScreenCapture.NET.PipeWire;

namespace ScreenCapture.NET;

/// <summary>
/// A PipeWire stream that receives the frames of a node (e.g. a monitor of a screen cast session) in mapped memory.
/// </summary>
/// <remarks>
/// <para>
/// PipeWire calls it on the thread of its <see cref="PipeWireCore"/>: every buffer that arrives replaces the newest one (the older goes back to PipeWire right away),
/// <see cref="TakeNewestFrame"/> makes the newest the current one (the frame the zones are read from) and gives the previous current one back.
/// The capture reads the zones from the current one without copying it (under <see cref="BufferLock"/>): PipeWire doesn't write into a buffer while the stream holds it.
/// </para>
/// <para>
/// Lock order: the lock of the core (<see cref="PipeWireCore.Enter"/>) before <see cref="BufferLock"/> and the frame lock, which are never held at the same time.
/// </para>
/// </remarks>
internal sealed unsafe class PipeWireStream : IDisposable
{
    #region Constants

    private const string PROPERTIES = "media.type=Video media.category=Capture media.role=Screen";

    /// <summary>
    /// How long a new stream may take to start streaming (it usually takes some 10 ms).
    /// PipeWire reports a node that's gone or a format both ends don't support as an error of the stream; this catches a stream that hangs without one.
    /// </summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a stream that streamed may stay paused (e.g. while it negotiates a new size) before it counts as failed: a stream whose node was removed is paused, without an error.
    /// </summary>
    public static readonly TimeSpan PauseTimeout = TimeSpan.FromSeconds(2);

    #endregion

    #region Properties & Fields

    private readonly PipeWireCore _core;

    // Only used under the lock of the PipeWire thread (on it or through PipeWireCore.Enter).
    private nint _stream;
    private GCHandle _handle;
    private PwStreamEvents* _events;
    private void* _hook;

    // Only used on the PipeWire thread: the negotiated format.
    private uint _videoFormat;
    private int _width;
    private int _height;

    // Guards the fields below; waited on with Monitor. A leaf lock.
    private readonly object _frameLock = new();
    private PwBuffer* _newest;
    private PipeWireFrame _newestFrame;
    private Exception? _failure;
    private int _state = PW_STREAM_STATE_UNCONNECTED;
    private long _stateTimestamp = Stopwatch.GetTimestamp();
    private bool _hasStreamed;
    private int _frameWidth;
    private int _frameHeight;

    // Only used under BufferLock (and swapped under the lock of the PipeWire thread too).
    private PwBuffer* _current;
    private PipeWireFrame? _currentFrame;

    /// <summary>
    /// Gets the lock held while the current frame is read (<see cref="CurrentFrame"/>). A leaf lock.
    /// </summary>
    public Lock BufferLock { get; } = new();

    /// <summary>
    /// Gets the frame the zones are read from; <c>null</c> before the first <see cref="TakeNewestFrame"/>, and after PipeWire removed its buffer. Only valid under <see cref="BufferLock"/>.
    /// </summary>
    public PipeWireFrame? CurrentFrame
    {
        get
        {
            Debug.Assert(BufferLock.IsHeldByCurrentThread);
            return _currentFrame;
        }
    }

    /// <summary>
    /// Gets why the stream doesn't deliver frames anymore (e.g. PipeWire reported an error, the session ended); <c>null</c> while it works.
    /// </summary>
    public Exception? Failure
    {
        get
        {
            lock (_frameLock)
                return _failure;
        }
    }

    /// <summary>
    /// Gets the size of the frames as negotiated with the compositor; 0x0 before the format is agreed.
    /// </summary>
    /// <remarks>
    /// The format is agreed before the stream streams: once <see cref="WaitUntilStreaming"/> succeeded, this is the size of the frames.
    /// </remarks>
    public (int Width, int Height) FrameSize
    {
        get
        {
            lock (_frameLock)
                return (_frameWidth, _frameHeight);
        }
    }

    #endregion

    #region Constructors

    private PipeWireStream(PipeWireCore core)
    {
        this._core = core;
    }

    #endregion

    #region Methods

    // Called by PipeWireCore under the lock of the PipeWire thread.
    internal static PipeWireStream Connect(PipeWireCore core, nint pwCore, uint nodeId, int width, int height)
    {
        PipeWireStream stream = new(core);
        try
        {
            stream._stream = pw_stream_new(pwCore, "ScreenCapture.NET", pw_properties_new_string(PROPERTIES));
            if (stream._stream == 0) throw new InvalidOperationException("pw_stream_new failed.");

            stream._handle = GCHandle.Alloc(stream);
            stream._events = (PwStreamEvents*)NativeMemory.AllocZeroed((nuint)sizeof(PwStreamEvents));
            stream._events->version = PW_VERSION_STREAM_EVENTS;
            stream._events->state_changed = &OnStateChanged;
            stream._events->param_changed = &OnParamChanged;
            stream._events->remove_buffer = &OnRemoveBuffer;
            stream._events->process = &OnProcess;
            stream._hook = NativeMemory.AllocZeroed(SPA_HOOK_SIZE);
            pw_stream_add_listener(stream._stream, stream._hook, stream._events, GCHandle.ToIntPtr(stream._handle));

            byte[] format = SpaPod.BuildEnumFormat(width, height);
            fixed (byte* formatPointer = format)
            {
                byte* parameter = formatPointer;
                int result = pw_stream_connect(stream._stream, PW_DIRECTION_INPUT, nodeId, PW_STREAM_FLAG_AUTOCONNECT | PW_STREAM_FLAG_MAP_BUFFERS, &parameter, 1);
                if (result < 0) throw new InvalidOperationException($"Connecting the PipeWire stream to node {nodeId} failed ({result}).");
            }

            return stream;
        }
        catch
        {
            stream.DestroyLocked(null);
            throw;
        }
    }

    /// <summary>
    /// Waits until the stream streams (up to <see cref="StartTimeout"/>).
    /// </summary>
    /// <returns><c>null</c> if it streams; otherwise why not.</returns>
    public Exception? WaitUntilStreaming()
    {
        long deadline = Stopwatch.GetTimestamp() + ToTicks(StartTimeout);
        lock (_frameLock)
            while (true)
            {
                if (_failure != null) return _failure;
                if (_state == PW_STREAM_STATE_STREAMING) return null;

                long remaining = deadline - Stopwatch.GetTimestamp();
                if (remaining <= 0)
                    return _failure = new InvalidOperationException($"The PipeWire stream didn't start within {StartTimeout.TotalSeconds} s (it's {GetStateName(_state)}): its node is gone or offers no supported format.");

                Wait(remaining);
            }
    }

    /// <summary>
    /// Waits until a frame arrived that wasn't taken yet, or the stream failed (also when it stays paused longer than <see cref="PauseTimeout"/>).
    /// </summary>
    /// <param name="deadline">Until when to wait, as a <see cref="Stopwatch"/> timestamp.</param>
    /// <returns><c>true</c> if a frame is there; <c>false</c> if none arrived in time or the stream failed (see <see cref="Failure"/>).</returns>
    public bool WaitForFrame(long deadline)
    {
        lock (_frameLock)
            while (true)
            {
                if (_failure != null) return false;
                if (_newest != null) return true;

                long now = Stopwatch.GetTimestamp();
                long waitUntil = deadline;
                if (_hasStreamed && (_state != PW_STREAM_STATE_STREAMING))
                {
                    long pauseEnd = _stateTimestamp + ToTicks(PauseTimeout);
                    if (now >= pauseEnd)
                    {
                        _failure = new InvalidOperationException($"The PipeWire stream is {GetStateName(_state)} for more than {PauseTimeout.TotalSeconds} s: its node was probably removed.");
                        return false;
                    }

                    waitUntil = Math.Min(deadline, pauseEnd);
                }

                if (now >= deadline) return false;

                Wait(waitUntil - now);
            }
    }

    // Under _frameLock. Rounded up to whole milliseconds: waking before the end would only wait again.
    private void Wait(long ticks) => Monitor.Wait(_frameLock, (int)Math.Min(int.MaxValue, ((ticks * 1000) + Stopwatch.Frequency - 1) / Stopwatch.Frequency));

    private static long ToTicks(TimeSpan time) => (long)(time.TotalSeconds * Stopwatch.Frequency);

    private static string GetStateName(int state)
        => state switch
        {
            PW_STREAM_STATE_ERROR => "failed",
            PW_STREAM_STATE_UNCONNECTED => "unconnected",
            PW_STREAM_STATE_CONNECTING => "connecting",
            PW_STREAM_STATE_PAUSED => "paused",
            PW_STREAM_STATE_STREAMING => "streaming",
            _ => $"in state {state}"
        };

    /// <summary>
    /// Makes the newest frame the current one and gives the previous current one back to PipeWire.
    /// </summary>
    /// <returns>The frame; <c>null</c> if there's none (anymore, e.g. PipeWire removed its buffer meanwhile) or the stream was destroyed.</returns>
    public PipeWireFrame? TakeNewestFrame()
    {
        using PipeWireCore.Scope scope = _core.Enter();
        if (!scope.IsAlive || (_stream == 0)) return null;

        PwBuffer* newest;
        PipeWireFrame frame;
        lock (_frameLock)
        {
            newest = _newest;
            frame = _newestFrame;
            _newest = null;
        }

        if (newest == null) return null;

        PwBuffer* previous;
        lock (BufferLock)
        {
            previous = _current;
            _current = newest;
            _currentFrame = frame;
        }

        if (previous != null)
            _ = pw_stream_queue_buffer(_stream, previous);

        return frame;
    }

    /// <summary>
    /// Disconnects and destroys the stream.
    /// </summary>
    public void Dispose()
    {
        using PipeWireCore.Scope scope = _core.Enter();

        // A core that was disposed destroyed its streams already.
        if (!scope.IsAlive) return;

        DestroyLocked(new ObjectDisposedException(nameof(PipeWireStream)));
        _core.Remove(this);
    }

    /// <summary>
    /// Destroys the stream; with the lock of the PipeWire thread held (or before its thread started).
    /// </summary>
    /// <param name="reason">The <see cref="Failure"/> from now on, unless it already failed.</param>
    internal void DestroyLocked(Exception? reason)
    {
        // Nothing reads the frames afterwards; PipeWire frees all buffers with the stream.
        lock (BufferLock)
        {
            _current = null;
            _currentFrame = null;
        }

        lock (_frameLock)
        {
            _newest = null;
            _failure ??= reason;
            Monitor.PulseAll(_frameLock);
        }

        // PipeWire calls the events while it destroys the stream (e.g. remove_buffer): the handle is freed afterwards.
        if (_stream != 0)
            pw_stream_destroy(_stream);
        _stream = 0;

        if (_events != null) NativeMemory.Free(_events);
        if (_hook != null) NativeMemory.Free(_hook);
        if (_handle.IsAllocated) _handle.Free();
        _events = null;
        _hook = null;
    }

    private void Fail(Exception exception)
    {
        lock (_frameLock)
        {
            _failure ??= exception;
            Monitor.PulseAll(_frameLock);
        }
    }

    private static PipeWireStream? FromData(nint data) => GCHandle.FromIntPtr(data).Target as PipeWireStream;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStateChanged(nint data, int oldState, int state, byte* error)
    {
        try
        {
            PipeWireStream? stream = FromData(data);
            if (stream == null) return;

            lock (stream._frameLock)
            {
                stream._state = state;
                stream._stateTimestamp = Stopwatch.GetTimestamp();
                stream._hasStreamed |= state == PW_STREAM_STATE_STREAMING;
                Monitor.PulseAll(stream._frameLock);
            }

            // E.g. the compositor removed the node (the user stopped sharing the monitor) or PipeWire went away.
            if (state == PW_STREAM_STATE_ERROR)
                stream.Fail(new InvalidOperationException($"The PipeWire stream failed: {(error != null ? Marshal.PtrToStringUTF8((nint)error) : "unknown error")}."));
            else if ((state == PW_STREAM_STATE_UNCONNECTED) && (oldState != PW_STREAM_STATE_UNCONNECTED))
                stream.Fail(new InvalidOperationException("The PipeWire stream was disconnected."));
        }
        catch
        {
            // Nothing may be thrown into PipeWire.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnParamChanged(nint data, uint id, byte* param)
    {
        try
        {
            if ((id != SPA_PARAM_Format) || (param == null)) return;

            PipeWireStream? stream = FromData(data);
            if (stream == null) return;

            ReadOnlySpan<byte> pod = new(param, 8 + (int)*(uint*)param);
            if (!SpaPod.TryReadVideoFormat(pod, out uint videoFormat, out int width, out int height)
             || (videoFormat is not (SPA_VIDEO_FORMAT_BGRx or SPA_VIDEO_FORMAT_BGRA)) || (width <= 0) || (height <= 0))
            {
                stream.Fail(new NotSupportedException($"The compositor negotiated an unsupported format (video format {videoFormat}, {width}x{height}). Only BGRx and BGRA are supported."));
                return;
            }

            stream._videoFormat = videoFormat;
            stream._width = width;
            stream._height = height;

            lock (stream._frameLock)
                (stream._frameWidth, stream._frameHeight) = (width, height);

            byte[] buffers = SpaPod.BuildBuffers();
            byte[] metaHeader = SpaPod.BuildMetaHeader();
            fixed (byte* buffersPointer = buffers)
            fixed (byte* metaHeaderPointer = metaHeader)
            {
                byte** parameters = stackalloc byte*[2] { buffersPointer, metaHeaderPointer };
                _ = pw_stream_update_params(stream._stream, parameters, 2);
            }
        }
        catch
        {
            // Nothing may be thrown into PipeWire.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRemoveBuffer(nint data, PwBuffer* buffer)
    {
        try
        {
            PipeWireStream? stream = FromData(data);
            if (stream == null) return;

            // E.g. on a new format: the memory is unmapped after this returns.
            lock (stream._frameLock)
                if (stream._newest == buffer)
                    stream._newest = null;

            lock (stream.BufferLock)
                if (stream._current == buffer)
                {
                    stream._current = null;
                    stream._currentFrame = null;
                }
        }
        catch
        {
            // Nothing may be thrown into PipeWire.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnProcess(nint data)
    {
        try
        {
            PipeWireStream? stream = FromData(data);
            if (stream == null) return;

            // Only the newest frame is kept: the others go back right away, so the compositor always has a buffer to write the next one into.
            PwBuffer* newest = null;
            PipeWireFrame frame = default;
            PwBuffer* buffer;
            while ((buffer = pw_stream_dequeue_buffer(stream._stream)) != null)
            {
                if (TryReadFrame(buffer, stream._width, stream._height, stream._videoFormat, out PipeWireFrame bufferFrame))
                {
                    if (newest != null)
                        _ = pw_stream_queue_buffer(stream._stream, newest);

                    newest = buffer;
                    frame = bufferFrame;
                }
                else
                    _ = pw_stream_queue_buffer(stream._stream, buffer);
            }

            if (newest == null) return;

            PwBuffer* dropped;
            lock (stream._frameLock)
            {
                dropped = stream._newest;
                stream._newest = newest;
                stream._newestFrame = frame;
                Monitor.PulseAll(stream._frameLock);
            }

            if (dropped != null)
                _ = pw_stream_queue_buffer(stream._stream, dropped);
        }
        catch
        {
            // Nothing may be thrown into PipeWire.
        }
    }

    /// <summary>
    /// Reads the frame of a buffer of the stream, checking that the pixels lie within its mapped memory.
    /// </summary>
    /// <remarks>
    /// A buffer without (valid) pixels is no frame: e.g. one the compositor marked as corrupted, or (with cursor metadata) one that only moved the pointer.
    /// The buffer is described by the producer: its chunk (offset and stride) is checked against the size of the memory, so a malformed one can't make the frame reach past it.
    /// </remarks>
    /// <param name="buffer">The dequeued buffer.</param>
    /// <param name="width">The negotiated width of the frames.</param>
    /// <param name="height">The negotiated height of the frames.</param>
    /// <param name="videoFormat">The negotiated video format (<see cref="SPA_VIDEO_FORMAT_BGRA"/> or <see cref="SPA_VIDEO_FORMAT_BGRx"/>).</param>
    /// <param name="frame">The frame; <c>default</c> if there's none.</param>
    /// <returns><c>true</c> if the buffer holds a frame.</returns>
    internal static bool TryReadFrame(PwBuffer* buffer, int width, int height, uint videoFormat, out PipeWireFrame frame)
    {
        frame = default;

        SpaBuffer* spaBuffer = buffer->buffer;
        if ((spaBuffer == null) || (spaBuffer->n_datas < 1) || (spaBuffer->datas == null) || (width <= 0) || (height <= 0)) return false;

        SpaData* spaData = spaBuffer->datas;
        if ((spaData->data == null) || (spaData->chunk == null)) return false;

        SpaChunk chunk = *spaData->chunk;
        if (((chunk.flags & SPA_CHUNK_FLAG_CORRUPTED) != 0) || (chunk.size == 0)) return false;

        // The last row may end without padding.
        long rowSize = (long)width * 4;
        long stride = chunk.stride > 0 ? chunk.stride : rowSize;
        if ((stride < rowSize) || ((chunk.offset + (stride * (height - 1)) + rowSize) > spaData->maxsize)) return false;

        long pts = 0;
        for (uint i = 0; (i < spaBuffer->n_metas) && (spaBuffer->metas != null); i++)
        {
            SpaMeta meta = spaBuffer->metas[i];
            if ((meta.type != SPA_META_Header) || (meta.data == null) || (meta.size < sizeof(SpaMetaHeader))) continue;

            SpaMetaHeader* header = (SpaMetaHeader*)meta.data;
            if ((header->flags & SPA_META_HEADER_FLAG_CORRUPTED) != 0) return false;

            pts = header->pts;
        }

        frame = new PipeWireFrame((nint)(spaData->data + chunk.offset), width, height, (int)stride, videoFormat == SPA_VIDEO_FORMAT_BGRA, ToStopwatchTimestamp(pts));
        return true;
    }

    /// <summary>
    /// Converts the presentation time of a frame (nanoseconds of CLOCK_MONOTONIC, the clock of the <see cref="Stopwatch"/> on Linux) to a <see cref="Stopwatch"/> timestamp; 0 if there's none.
    /// </summary>
    internal static long ToStopwatchTimestamp(long pts) => pts <= 0 ? 0 : (long)(((Int128)pts * Stopwatch.Frequency) / 1_000_000_000);

    #endregion
}

/// <summary>
/// A frame of a <see cref="PipeWireStream"/>: its pixels in the mapped buffer.
/// </summary>
/// <param name="Data">The first pixel.</param>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
/// <param name="Stride">The bytes from one row to the next.</param>
/// <param name="HasAlpha">Whether the fourth byte is alpha (BGRA); otherwise it's padding (BGRx).</param>
/// <param name="Timestamp">When the compositor produced the frame, as a <see cref="Stopwatch"/> timestamp; 0 if it didn't tell.</param>
internal readonly record struct PipeWireFrame(nint Data, int Width, int Height, int Stride, bool HasAlpha, long Timestamp);
