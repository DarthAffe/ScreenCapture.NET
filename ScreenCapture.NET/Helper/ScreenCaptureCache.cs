using System;
using System.Collections.Generic;
using System.Threading;

namespace ScreenCapture.NET;

/// <summary>
/// Holds the captures of a <see cref="IScreenCaptureService"/>: one per display (by <see cref="Display.Id"/>), created when it's first requested.
/// </summary>
/// <remarks>
/// Implements the caching rules of <see cref="IScreenCaptureService.GetScreenCapture"/>, for services of other backends too.
/// Thread-safe, concurrent requests for the same display get the same capture. A disposed capture is replaced the next time it's requested.
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
    /// <param name="owner">The service owning the captures, named by the <see cref="ObjectDisposedException"/>.</param>
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
    /// Requests for other displays wait while a capture is created. If the creation throws, nothing is cached.
    /// </remarks>
    /// <param name="display">The display.</param>
    /// <param name="options">The options of a new capture.</param>
    /// <param name="requireEqualOptions"><c>true</c> to return an existing capture only if it was created with equal options.</param>
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
    /// Disposes all captures. Afterwards <see cref="GetOrCreate"/> throws.
    /// </summary>
    /// <remarks>
    /// Call it from the Dispose of the service, and release the resources the captures share only if it returns <c>true</c>.
    /// </remarks>
    /// <returns><c>true</c> on the first call; otherwise <c>false</c>.</returns>
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

        // Outside the lock: disposing waits for a running capture.
        foreach (TCapture capture in captures)
            capture.Dispose();

        return true;
    }

    #endregion
}
