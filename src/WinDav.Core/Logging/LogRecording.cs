// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;

namespace WinDav.Core.Logging;

/// <summary>
/// A spell of loud logging: one of the two upper levels, over some of the areas, for a while.
/// </summary>
/// <remarks>
/// <para>
/// The three lower levels are always on and are nobody's decision. These two are a decision,
/// and it is one a person makes while something is going wrong. So it is bounded twice over:
/// by the time it was given, at most <see cref="MaximumDuration"/> of it, and by what it may
/// write, <see cref="MaximumBytes"/>. Whichever comes first ends it, and it does not begin
/// again. A trace left on by accident is a full disk a week later, and a mount that has been
/// running for a week is exactly the one worth tracing.
/// </para>
/// <para>
/// It says so in the file, twice: a line when it starts naming both limits, and a line when
/// it stops naming the reason and how much it wrote. Between them the reader has the whole
/// of what was recorded, and outside them the file is as quiet as it always is. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#74-logging-five-levels-four-areas-and-a-switch-that-turns-itself-off">decision 74</see>.
/// </para>
/// </remarks>
public sealed class LogRecording : IDisposable
{
    /// <summary>
    /// How long a recording runs when no time was asked for.
    /// </summary>
    /// <remarks>
    /// A minute is what it takes to do the thing that fails once more, and it is short enough
    /// that forgetting about it costs nothing.
    /// </remarks>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The longest a recording may be asked to run.
    /// </summary>
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(1);

    /// <summary>
    /// The most a recording may write before it stops.
    /// </summary>
    /// <remarks>
    /// The same figure as <see cref="LogFile.MaximumFileBytes"/>, so that what was recorded
    /// is in one file and a reader is not sent looking for the other half of it.
    /// </remarks>
    public const long MaximumBytes = 16L * 1024 * 1024;

    // Held for the whole of a change, because the clock runs on a thread of the pool while
    // the areas are writing on every other one.
    private readonly Lock _gate = new();

    private readonly LogFile _file;
    private readonly LogArea[] _areas;
    private readonly Timer _clock;

    private long _bytes;
    private long _records;
    private bool _running = true;

    /// <summary>
    /// Initialises a new instance of the <see cref="LogRecording"/> class and starts it.
    /// </summary>
    /// <param name="file">Where the records go, and where the two lines about them go.</param>
    /// <param name="level">Debug or Trace. The lower levels do not need asking for.</param>
    /// <param name="areas">The areas it covers. Every area when it is empty.</param>
    /// <param name="duration">How long it runs, at most <see cref="MaximumDuration"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> or <paramref name="areas"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="level"/> is not one of the two upper levels, or
    /// <paramref name="duration"/> is not a length of time between nothing and
    /// <see cref="MaximumDuration"/>.
    /// </exception>
    public LogRecording(LogFile file, LogLevel level, IEnumerable<LogArea> areas, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(areas);

        if (level is not (LogLevel.Debug or LogLevel.Trace))
        {
            throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                "A recording is of debug or of trace. The levels below them are always on.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(duration, MaximumDuration);

        _file = file;
        _areas = [.. areas.Distinct()];

        if (_areas.Length == 0)
        {
            _areas = [.. LogAreas.All];
        }

        Level = level;
        Duration = duration;

        _file.Note(DateTimeOffset.Now, LogFormat.RecordingStart(level, _areas, duration, MaximumBytes));

        // One shot. Nothing restarts it, and the callback is the only place the time runs
        // out, so a recording nobody writes to still ends when it said it would.
        _clock = new Timer(_ => Finish(LogRecordingEnd.Duration), null, duration, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Gets the level being recorded.
    /// </summary>
    public LogLevel Level { get; }

    /// <summary>
    /// Gets how long it was given.
    /// </summary>
    public TimeSpan Duration { get; }

    /// <summary>
    /// Gets the areas it covers.
    /// </summary>
    public IReadOnlyList<LogArea> Areas => _areas;

    /// <summary>
    /// Gets how many records it has written.
    /// </summary>
    public long Records
    {
        get
        {
            lock (_gate)
            {
                return _records;
            }
        }
    }

    /// <summary>
    /// Gets why it stopped, and <see cref="LogRecordingEnd.None"/> while it is running.
    /// </summary>
    public LogRecordingEnd Ending { get; private set; }

    /// <summary>
    /// Asks whether a record would be recorded.
    /// </summary>
    /// <param name="area">Where it comes from.</param>
    /// <param name="level">How loud it is.</param>
    /// <returns><see langword="true"/> when this recording is what lets it through.</returns>
    public bool Covers(LogArea area, LogLevel level)
    {
        lock (_gate)
        {
            return _running && level >= Level && Array.IndexOf(_areas, area) >= 0;
        }
    }

    /// <summary>
    /// Counts a record that this recording let through.
    /// </summary>
    /// <param name="bytes">What it took in the file.</param>
    /// <remarks>
    /// The record is written first and counted afterwards, so the limit is the last thing
    /// over it rather than the last thing under it. A line either side of sixteen megabytes
    /// is not worth a byte of arithmetic before every write.
    /// </remarks>
    public void Note(int bytes)
    {
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _bytes += bytes;
            _records++;

            if (_bytes >= MaximumBytes)
            {
                Stop(LogRecordingEnd.Size);
            }
        }
    }

    /// <summary>
    /// Stops the recording, with the closing line, if it is still running.
    /// </summary>
    public void Dispose()
    {
        _clock.Dispose();

        Finish(LogRecordingEnd.Session);

        GC.SuppressFinalize(this);
    }

    private void Finish(LogRecordingEnd end)
    {
        lock (_gate)
        {
            if (_running)
            {
                Stop(end);
            }
        }
    }

    // Under the lock, always: the closing line is written once, and what it counts is what
    // was counted up to it.
    private void Stop(LogRecordingEnd end)
    {
        _running = false;
        Ending = end;

        _file.Note(DateTimeOffset.Now, LogFormat.RecordingEnd(end, _records));
    }
}
