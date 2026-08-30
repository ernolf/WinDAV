// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace WinDav.Fs;

/// <summary>
/// How many requests one mount may have on the wire at the same time, and what happens when
/// the server says it will not take another.
/// </summary>
/// <remarks>
/// <para>
/// A ceiling, never a target: nothing here starts a request in order to fill the room. What
/// it does is hold a thread back until there is room, which is what turns the parallelism
/// WinFsp dispatches with into the number the server was measured to want.
/// </para>
/// <para>
/// A width found once was found against a server that was otherwise idle, and no server stays
/// that way. So a refusal lowers the number at once and it is raised again slowly, never past
/// the number it was given. Answering a refusal by trying harder is how a shared server ends
/// up with a rule against the program, and it costs bytes rather than winning them. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#75-the-read-path-read-ahead-keep-attributes-briefly-and-let-the-server-set-the-width">decision 75</see>.
/// </para>
/// </remarks>
internal sealed class RequestGate
{
    // How long the server has to keep taking requests before one more is allowed. Load is a
    // property of the moment rather than of how many requests have gone by since, so the
    // clock is what the recovery is counted in. Long enough that a server which is busy for
    // a minute is not prodded four times while it is.
    private static readonly TimeSpan s_recovery = TimeSpan.FromSeconds(30);

    // Monitor rather than a semaphore, because the width changes while threads are waiting on
    // it and because a semaphore would have to be disposed of by a file system WinFsp never
    // asks to dispose.
    private readonly object _sync = new();

    private readonly ILogger _log;
    private readonly int _most;
    private readonly TimeSpan _recovery;

    private int _width;
    private int _inFlight;
    private long _changed;

    /// <summary>
    /// Initialises a new instance of the <see cref="RequestGate"/> class.
    /// </summary>
    /// <param name="most">
    /// The most that may be in flight at once. Anything below one is one, which is the value
    /// that switches the idea off: a request at a time.
    /// </param>
    /// <param name="log">Where a change of width is written down.</param>
    /// <param name="recovery">
    /// How long without a refusal before the width is raised again, or
    /// <see langword="null"/> for the interval this class was built with.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="log"/> is null.</exception>
    internal RequestGate(int most, ILogger log, TimeSpan? recovery = null)
    {
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
        _most = Math.Max(most, 1);
        _recovery = recovery ?? s_recovery;
        _width = _most;
        _changed = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Gets how many are allowed at this moment, which is the width it was given until a
    /// server refuses one.
    /// </summary>
    internal int Width
    {
        get
        {
            lock (_sync)
            {
                return _width;
            }
        }
    }

    /// <summary>
    /// Waits until there is room for one more request, and takes it.
    /// </summary>
    /// <remarks>
    /// Every call is answered by exactly one <see cref="Leave"/>, from a finally block, or
    /// the room is never given back.
    /// </remarks>
    internal void Enter()
    {
        lock (_sync)
        {
            while (_inFlight >= _width)
            {
                Monitor.Wait(_sync);
            }

            _inFlight++;
        }
    }

    /// <summary>
    /// Gives the room back, and says whether the server refused the request that had it.
    /// </summary>
    /// <param name="refused">
    /// Whether the store answered that it is busy. That is the one answer this reacts to: a
    /// missing file or a wrong credential says nothing about how much the server can take.
    /// </param>
    internal void Leave(bool refused)
    {
        lock (_sync)
        {
            _inFlight--;

            if (refused)
            {
                Lower();
            }
            else
            {
                RaiseIfDue();
            }

            // All of them, because the width may have grown by one and there is no telling
            // which waiter would fit into the room that opened.
            Monitor.PulseAll(_sync);
        }
    }

    // Called under the lock. The clock is restarted whether or not there was room to give
    // up, so a server that keeps refusing keeps the client at one request at a time.
    private void Lower()
    {
        _changed = Stopwatch.GetTimestamp();

        if (_width <= 1)
        {
            return;
        }

        _width--;

        // Not debug: this is a lasting change to the way the mount behaves, the same kind of
        // thing as the mount going up, and whoever reads the file afterwards needs it there
        // whether they thought to ask for a recording or not.
        if (_log.IsEnabled(LogLevel.Information))
        {
            _log.LogInformation(
                "The server would not take the request. {Width} at a time from now on.",
                _width);
        }
    }

    // Called under the lock, on the way out of a request the server did answer.
    private void RaiseIfDue()
    {
        if (_width >= _most || Stopwatch.GetElapsedTime(_changed) < _recovery)
        {
            return;
        }

        _width++;
        _changed = Stopwatch.GetTimestamp();

        if (_log.IsEnabled(LogLevel.Information))
        {
            _log.LogInformation("The server has taken everything since; {Width} requests at a time again.", _width);
        }
    }
}
