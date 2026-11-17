using System;
using System.Collections.Generic;
using System.Threading;

namespace ScreenCapture.NET;

/// <summary>
/// Holds the captures of a <see cref="IScreenCaptureService"/>: one per display (by <see cref="Display.Id"/>), created when it's first requested.
/// </summary>
/// <remarks>
/// It implements the caching rules of <see cref="IScreenCaptureService.GetScreenCapture"/> and is used by the services of this library; services of other backends can use it too.
/// All members are thread-safe. Concurrent requests for the same display get the same capture.
/// A capture disposed by its user (<see cref="IScreenCapture.IsDisposed"/>) is replaced by a new one the next time it's requested.
/// </remarks>
/// <typeparam name="TCapture">The type of the captures.</typeparam>
/// <typeparam name="TOptions">The type of the options of a capture. Compared with <see cref="object.Equals(object)"/>, so a record is a good fit.</typeparam>
public sealed class ScreenCaptureCache<TCapture, TOptions>
    where TCapture : class, IScreenCapture
    where TOptions : class
{
    #region Properties & Fields

    private readonly Lock _lock = new();
    private readonly Dictionary<string, TCapture> _captures = new(StringComparer.Ordinal);

    private readonly object _owner;
    private readonly Func<Display, TOptions, TCapture> _create;
    private readonly Func<TCapture, TOptions> _getOptions;

    private bool _isDisposed;

    /// <summary>
    /// Gets a value indicating whether <see cref="DisposeAll"/> was called.
    /// </summary>
    public bool IsDisposed => Volatile.Read(ref _isDisposed);

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ScreenCaptureCache{TCapture, TOptions}"/> class.
    /// </summary>
    /// <param name="owner">The service owning the captures; named by the <see cref="ObjectDisposedException"/> after <see cref="DisposeAll"/>.</param>
    /// <param name="create">Creates a capture of the display with the options. Called under the lock of the cache.</param>
    /// <param name="getOptions">Gets the options a capture was created with.</param>
    public ScreenCaptureCache(object owner, Func<Display, TOptions, TCapture> create, Func<TCapture, TOptions> getOptions)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(getOptions);

        this._owner = owner;
        this._create = create;
        this._getOptions = getOptions;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Gets the capture of the display, or creates one with the given options if there's none (or it was disposed).
    /// </summary>
    /// <remarks>
    /// The lock is held while a capture is created, so requests for other displays wait for it.
    /// An exception of the creation is passed on and nothing is cached.
    /// </remarks>
    /// <param name="display">The display.</param>
    /// <param name="options">The options of a new capture.</param>
    /// <param name="requireEqualOptions">
    /// <c>true</c> to return an existing capture only if it was created with equal options;
    /// <c>false</c> to return an existing capture whatever its options.
    /// </param>
    /// <returns>The capture of the display.</returns>
    /// <exception cref="ObjectDisposedException">The cache was disposed (<see cref="DisposeAll"/>).</exception>
    /// <exception cref="InvalidOperationException"><paramref name="requireEqualOptions"/> is <c>true</c> and the capture of the display exists with other options.</exception>
    public TCapture GetOrCreate(Display display, TOptions options, bool requireEqualOptions)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, _owner);

            if (_captures.TryGetValue(display.Id, out TCapture? capture) && !capture.IsDisposed)
            {
                if (requireEqualOptions)
                {
                    TOptions existingOptions = _getOptions(capture);
                    if (!existingOptions.Equals(options))
                        throw new InvalidOperationException($"The capture of the display '{display.Id}' already exists with other options ({existingOptions}).");
                }

                return capture;
            }

            capture = _create(display, options);
            _captures[display.Id] = capture;
            return capture;
        }
    }

    /// <summary>
    /// Disposes all captures (also the ones their users disposed already); afterwards <see cref="GetOrCreate"/> throws.
    /// </summary>
    /// <remarks>
    /// Call it from the <see cref="IDisposable.Dispose"/> of the service, and release the resources the captures share (e.g. a DirectX factory) only if it returns <c>true</c>.
    /// </remarks>
    /// <returns><c>true</c> on the first call; <c>false</c> if the cache was disposed already (nothing is done then).</returns>
    public bool DisposeAll()
    {
        List<TCapture> captures;
        lock (_lock)
        {
            if (_isDisposed) return false;

            Volatile.Write(ref _isDisposed, true);
            captures = [.. _captures.Values];
            _captures.Clear();
        }

        // Outside the lock: disposing a capture waits for its running capture (e.g. up to the DX11 timeout).
        // No new capture can be created meanwhile, since the cache is marked as disposed.
        foreach (TCapture capture in captures)
            capture.Dispose();

        return true;
    }

    #endregion
}
