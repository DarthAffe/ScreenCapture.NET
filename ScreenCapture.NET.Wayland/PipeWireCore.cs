using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using static ScreenCapture.NET.PipeWire;

namespace ScreenCapture.NET;

/// <summary>
/// A connection to PipeWire (on the socket of a screen cast session) with its own thread, on which PipeWire calls the streams.
/// </summary>
/// <remarks>
/// Disposing it destroys its streams first.
/// <para>
/// Lock order: <see cref="Enter"/> (the managed lock, then the lock of the PipeWire thread) before the locks of the streams.
/// The PipeWire thread only takes the stream locks while holding its own, never the managed lock.
/// </para>
/// </remarks>
[SuppressMessage("Usage", "CA2216:Disposable types should declare finalizer", Justification = "The session disposes it. A finalizer can't stop the PipeWire thread safely, and streams keep it reachable until they're destroyed.")]
internal sealed unsafe class PipeWireCore : IDisposable
{
    #region Properties & Fields

    // Held by all users of the thread loop on other threads, so Dispose can't destroy it meanwhile.
    private readonly Lock _lock = new();

    private nint _loop;
    private nint _context;
    private nint _core;
    private readonly List<PipeWireStream> _streams = [];
    private bool _isDisposed;

    // The listener of the core's events, freed after the core is disconnected.
    private GCHandle _handle;
    private PwCoreEvents* _events;
    private void* _hook;
    private volatile bool _isDead;

    /// <summary>
    /// Gets a value indicating whether the connection to PipeWire broke (e.g. the daemon was restarted). The nodes of the session are gone with it.
    /// </summary>
    public bool IsDead => _isDead;

    #endregion

    #region Constructors

    private PipeWireCore() { }

    #endregion

    #region Methods

    /// <summary>
    /// Connects to PipeWire on a connected socket (e.g. the one of <c>OpenPipeWireRemote</c>).
    /// </summary>
    /// <param name="socket">The socket; it belongs to the connection afterwards (also if connecting fails).</param>
    /// <exception cref="InvalidOperationException">libpipewire couldn't be loaded or the connection failed.</exception>
    public static PipeWireCore Connect(SafeFileHandle socket)
    {
        PipeWireCore core = new();
        try
        {
            try
            {
                pw_init(0, 0);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                throw new InvalidOperationException("libpipewire-0.3.so.0 isn't installed.", ex);
            }

            core._loop = pw_thread_loop_new("ScreenCapture.NET", 0);
            if (core._loop == 0) throw new InvalidOperationException("pw_thread_loop_new failed.");

            core._context = pw_context_new(pw_thread_loop_get_loop(core._loop), 0, 0);
            if (core._context == 0) throw new InvalidOperationException("pw_context_new failed.");

            if (pw_thread_loop_start(core._loop) < 0) throw new InvalidOperationException("pw_thread_loop_start failed.");

            pw_thread_loop_lock(core._loop);
            try
            {
                int fd = (int)socket.DangerousGetHandle();
                socket.SetHandleAsInvalid();

                core._core = pw_context_connect_fd(core._context, fd, 0, 0);
                if (core._core == 0) throw new InvalidOperationException("Connecting to PipeWire (pw_context_connect_fd) failed.");

                core._handle = GCHandle.Alloc(core, GCHandleType.Weak);
                core._events = (PwCoreEvents*)NativeMemory.AllocZeroed((nuint)sizeof(PwCoreEvents));
                core._events->version = PW_VERSION_CORE_EVENTS;
                core._events->error = &OnError;
                core._hook = NativeMemory.AllocZeroed(SPA_HOOK_SIZE);
                if (AddCoreListener(core._core, core._hook, core._events, GCHandle.ToIntPtr(core._handle)) < 0)
                    throw new InvalidOperationException("Listening to the PipeWire connection failed.");
            }
            finally
            {
                pw_thread_loop_unlock(core._loop);
            }

            return core;
        }
        catch
        {
            socket.Dispose();
            core.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a stream that receives the frames of a node.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The connection was disposed (e.g. the session ended).</exception>
    /// <exception cref="InvalidOperationException">The stream couldn't be created.</exception>
    public PipeWireStream CreateStream(uint nodeId, int width, int height)
    {
        using Scope scope = Enter();
        ObjectDisposedException.ThrowIf(!scope.IsAlive, this);

        PipeWireStream stream = PipeWireStream.Connect(this, _core, nodeId, width, height);
        _streams.Add(stream);
        return stream;
    }

    /// <summary>
    /// Enters the lock of the PipeWire thread to use the streams from another thread. Not alive after the connection was disposed.
    /// </summary>
    public Scope Enter()
    {
        _lock.Enter();
        if (_isDisposed) return new Scope(this, false);

        pw_thread_loop_lock(_loop);
        return new Scope(this, true);
    }

    // Called under Enter by a stream that destroyed itself.
    internal void Remove(PipeWireStream stream) => _streams.Remove(stream);

    /// <summary>
    /// Destroys the streams and closes the connection.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (_loop != 0)
            {
                pw_thread_loop_lock(_loop);
                foreach (PipeWireStream stream in _streams)
                    stream.DestroyLocked(new InvalidOperationException("The PipeWire connection of the screen cast session was closed."));
                _streams.Clear();

                if (_core != 0)
                    _ = pw_core_disconnect(_core);
                pw_thread_loop_unlock(_loop);

                // Joins the thread: not while holding its lock.
                pw_thread_loop_stop(_loop);
            }

            if (_context != 0)
                pw_context_destroy(_context);
            if (_loop != 0)
                pw_thread_loop_destroy(_loop);

            if (_events != null) NativeMemory.Free(_events);
            if (_hook != null) NativeMemory.Free(_hook);
            if (_handle.IsAllocated) _handle.Free();
            _events = null;
            _hook = null;

            _core = 0;
            _context = 0;
            _loop = 0;
        }
    }

    // On the PipeWire thread. Only an error of the core itself (id 0) ends the connection, streams report their own.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnError(nint data, uint id, int seq, int result, byte* message)
    {
        try
        {
            if ((id == PW_ID_CORE) && (GCHandle.FromIntPtr(data).Target is PipeWireCore core))
                core._isDead = true;
        }
        catch
        {
            // Nothing may be thrown into PipeWire.
        }
    }

    #endregion

    /// <summary>
    /// Holds the lock of the connection (and of its PipeWire thread while it's alive) until disposed.
    /// </summary>
    public readonly ref struct Scope
    {
        private readonly PipeWireCore _core;

        /// <summary>
        /// Gets a value indicating whether the connection is alive, only then may PipeWire be used.
        /// </summary>
        public bool IsAlive { get; }

        internal Scope(PipeWireCore core, bool isAlive)
        {
            _core = core;
            IsAlive = isAlive;
        }

        public void Dispose()
        {
            if (IsAlive) pw_thread_loop_unlock(_core._loop);
            _core._lock.Exit();
        }
    }
}
