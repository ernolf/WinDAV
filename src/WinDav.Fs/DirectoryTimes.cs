// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using WinDav.Abstractions;

namespace WinDav.Fs;

/// <summary>
/// The times a directory was given, kept until the copy through it has stopped and then set
/// once more.
/// </summary>
/// <remarks>
/// <para>
/// Windows sets a directory's times the moment it makes it, before the first file goes into
/// it, and it says so once: there is no second call when the copy through it has finished,
/// and WinFsp has no signal for one. A store that works a directory's time out from what is
/// in it replaces what was set with the time of the file that landed last, so at the end of a
/// copy every directory carries the time of its last file while the files carry the times
/// they came with.
/// </para>
/// <para>
/// So the times are kept here for the life of the mount and every write under the name puts
/// off the moment they go again, an upload for as long as it is on its way. Once nothing has
/// been written under a directory for the quiet period, one request goes out for it, behind
/// everything anybody is waiting for; whatever lands under it afterwards puts it in again for
/// another one. One request per directory per copy through it, whatever the number of files:
/// a request behind every file is what must not happen.
/// See <see href="https://github.com/ernolf/WinDAV/issues/119">#119</see>.
/// </para>
/// </remarks>
internal sealed class DirectoryTimes
{
    // Milliseconds with one place, the same as the wire records and the reads are written
    // with, so that a request and the record of it can be laid side by side.
    private const string ElapsedFormat = "0.#";

    private readonly IStorageProvider _provider;
    private readonly TimeSpan _quiet;
    private readonly TimeSpan _interval;
    private readonly ILogger _log;

    // Held while the register is looked at. Nothing waits on a request inside it.
    private readonly Lock _sync = new();

    // Ordinal, for the reason the listings are ordinal: a store that keeps case has two
    // directories where two spellings differ.
    private readonly Dictionary<string, Waiting> _waiting = new(StringComparer.Ordinal);

    // The uploads that are on their way out, by the name each one is going to. A file of a
    // few megabytes takes longer to send than any sensible quiet period, and while it is
    // going the directory it lands in is about to be dated by it.
    private readonly Dictionary<string, int> _busy = new(StringComparer.Ordinal);

    // How many of the directories in the register carry times the store has not been told
    // since they were last written into. Nothing to send is what ends the round.
    private int _dirty;

    private bool _armed;
    private bool _stopped;

    /// <summary>
    /// Initialises a new instance of the <see cref="DirectoryTimes"/> class.
    /// </summary>
    /// <param name="provider">The store the times go to.</param>
    /// <param name="quiet">
    /// How long nothing may have been written under a directory before the times it was given
    /// are set again.
    /// </param>
    /// <param name="log">Where a request of this kind is written down.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="quiet"/> is nothing at all, or less. A register that waits for no
    /// quiet would send behind every file, which is the one thing it is built not to do;
    /// <see cref="Over"/> leaves it out.
    /// </exception>
    internal DirectoryTimes(IStorageProvider provider, TimeSpan quiet, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(quiet, TimeSpan.Zero);

        _provider = provider;
        _quiet = quiet;
        _log = log;

        // Half the quiet period, the way a listing is renewed at half its life: the register
        // is looked at often enough that a directory goes within half a period of falling
        // quiet, and a period that was given in milliseconds still leaves something to wait.
        _interval = TimeSpan.FromTicks(Math.Max(quiet.Ticks / 2, TimeSpan.TicksPerMillisecond));
    }

    /// <summary>
    /// Makes a register for a mount, or leaves it out.
    /// </summary>
    /// <param name="provider">The store the times go to.</param>
    /// <param name="quiet">
    /// The quiet period, or nothing at all to leave a directory carrying what the copy into it
    /// made of its times.
    /// </param>
    /// <param name="log">Where a request of this kind is written down.</param>
    /// <returns>The register, or <see langword="null"/> where there is to be none.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal static DirectoryTimes? Over(IStorageProvider provider, TimeSpan quiet, ILogger log) =>
        quiet <= TimeSpan.Zero ? null : new DirectoryTimes(provider, quiet, log);

    /// <summary>
    /// Writes down the times a directory has just been given.
    /// </summary>
    /// <param name="path">The directory, as the store spells it.</param>
    /// <param name="times">What it was told to carry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    internal void Given(string path, EntryTimes times)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (times.IsEmpty)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();

        lock (_sync)
        {
            if (_stopped)
            {
                return;
            }

            // The request that has just carried these times is itself a write into the
            // directory above, so what is remembered up there waits from now as well.
            Touch(path, now);

            Keep(path, new Waiting(times, now, Sent: false));

            Arm();
        }
    }

    /// <summary>
    /// Notes that a file is on its way to the store.
    /// </summary>
    /// <param name="path">What is being written, as the store spells it.</param>
    /// <remarks>
    /// An upload dates the directory it lands in the moment it arrives, and a large one takes
    /// longer to send than a sensible quiet period is: a directory whose times went out while
    /// one of its files was still going up would be dated by that file afterwards. So a name
    /// on its way keeps everything above it waiting until <see cref="Wrote"/> says it is done.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    internal void Writing(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        long now = Stopwatch.GetTimestamp();

        lock (_sync)
        {
            _busy[path] = _busy.TryGetValue(path, out int going) ? going + 1 : 1;

            Touch(path, now);

            Arm();
        }
    }

    /// <summary>
    /// Notes that something under a name has changed at the store.
    /// </summary>
    /// <param name="path">What was written, as the store spells it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    internal void Wrote(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        long now = Stopwatch.GetTimestamp();

        lock (_sync)
        {
            // Whatever was said to be on its way has arrived. An upload that failed comes
            // here as well: it has not dated anything, and either way it is not on its way
            // any more and must not hold a directory back for the life of the mount.
            if (_busy.TryGetValue(path, out int going))
            {
                if (going > 1)
                {
                    _busy[path] = going - 1;
                }
                else
                {
                    _busy.Remove(path);
                }
            }

            Touch(path, now);

            Arm();
        }
    }

    /// <summary>
    /// Notes that a name is not there any more.
    /// </summary>
    /// <param name="path">What went, as the store spells it.</param>
    /// <remarks>
    /// What was remembered under the name goes with the name: there is nothing left to set a
    /// time on, and a request for it would be one the store answers with 404. The directory it
    /// stood in has changed, so it waits from now like any other write.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    internal void Gone(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        long now = Stopwatch.GetTimestamp();
        string below = path.EndsWith('/') ? path : path + "/";

        lock (_sync)
        {
            Touch(path, now);

            Drop(path);

            List<string> under = [];

            foreach (string held in _waiting.Keys)
            {
                if (held.StartsWith(below, StringComparison.Ordinal))
                {
                    under.Add(held);
                }
            }

            foreach (string held in under)
            {
                Drop(held);
            }
        }
    }

    /// <summary>
    /// Sends the times of every directory that has been quiet for long enough.
    /// </summary>
    /// <returns>The task of the sending, which nobody is waiting on.</returns>
    internal Task SendQuietAsync() => SendAsync(everything: false);

    /// <summary>
    /// Takes the register out of use and sends what is still in it.
    /// </summary>
    /// <remarks>
    /// The mount coming down is what calls this. A copy that ended a moment before it has
    /// directories in here that have not been quiet for the whole period yet, and this is
    /// their only chance to be set: what it costs is one request per directory still waiting,
    /// paid while the drive goes away.
    /// </remarks>
    internal void Stop()
    {
        lock (_sync)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
        }

        SendAsync(everything: true).GetAwaiter().GetResult();
    }

    // Every directory the write was in, up to the root. A store that works a directory's
    // time out from what is in it carries a write all the way up, so a file landing deep in a
    // tree is a write into every directory above it: every one of them waits again, and one
    // that has already been told carries the wrong time again and is to be told once more.
    //
    // Except what is in the round the write is itself a part of. A round goes deepest first,
    // so a directory in it is set after the one below it has been and needs nothing from it.
    private void Touch(string path, long now, HashSet<string>? round = null)
    {
        if (_waiting.Count == 0)
        {
            return;
        }

        for (string? above = ParentOf(path); above is not null; above = ParentOf(above))
        {
            if (round?.Contains(above) == true)
            {
                continue;
            }

            if (_waiting.TryGetValue(above, out Waiting held))
            {
                Keep(above, held with { Stamp = now, Sent = false });
            }
        }
    }

    // The register and the count of what is in it that has still to go, in one place. Every
    // entry goes in through here and out through Drop, and nowhere else.
    private void Keep(string path, Waiting entry)
    {
        if (_waiting.TryGetValue(path, out Waiting held) && !held.Sent)
        {
            _dirty--;
        }

        _waiting[path] = entry;

        if (!entry.Sent)
        {
            _dirty++;
        }
    }

    private void Drop(string path)
    {
        if (_waiting.Remove(path, out Waiting held) && !held.Sent)
        {
            _dirty--;
        }
    }

    // Under the lock, and only from where the register has something in it. A mount nobody
    // copies into never starts the round at all.
    private void Arm()
    {
        if (_armed || _stopped || _dirty == 0)
        {
            return;
        }

        _armed = true;

        _ = Task.Run(SweepAsync, CancellationToken.None);
    }

    // One round at a time, and nothing anybody waits on. A store that is slow to answer
    // holds up the next round rather than having one sent after it, and what one round does
    // not get to is still in the register for the one after.
    private async Task SweepAsync()
    {
        while (true)
        {
            await Task.Delay(_interval).ConfigureAwait(false);

            lock (_sync)
            {
                // Over once there is nothing left to send, so that a mount that is done
                // copying costs nothing at all until the next directory is written into.
                if (_stopped || _dirty == 0)
                {
                    _armed = false;

                    return;
                }
            }

            await SendAsync(everything: false).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(bool everything)
    {
        while (true)
        {
            List<Ready> ready = Take(everything);

            if (ready.Count == 0)
            {
                return;
            }

            HashSet<string> round = new(ready.Count, StringComparer.Ordinal);

            foreach (Ready directory in ready)
            {
                round.Add(directory.Path);
            }

            foreach (Ready directory in ready)
            {
                long started = Stopwatch.GetTimestamp();

                try
                {
                    await _provider.SetTimesAsync(directory.Path, directory.Times).ConfigureAwait(false);
                }
                catch (ProviderException failure)
                {
                    // Written at a level nobody has to switch on, the way a time that would
                    // not go on a file is. Nobody asked for this request, so there is nobody
                    // to tell.
                    _log.LogWarning(
                        "Setting the times on {Path} again failed after {Elapsed} ms: {Reason}.",
                        directory.Path,
                        Elapsed(started),
                        failure.Error);

                    continue;
                }

                // This request is itself a write into the directory above, which is how a
                // directory that was set before this one goes in again for the next round.
                lock (_sync)
                {
                    Touch(directory.Path, Stopwatch.GetTimestamp(), round);

                    Arm();
                }

                if (_log.IsEnabled(LogLevel.Debug))
                {
                    _log.LogDebug(
                        "Set the times on {Path} again in {Elapsed} ms.",
                        directory.Path,
                        Elapsed(started));
                }
            }

            // What the round has put back waits the quiet period out like any other write.
            // The mount coming down has no period left to wait in, so it goes round until
            // there is nothing left: a round only ever puts back what is above what it sent,
            // and the root is where that ends.
            if (!everything)
            {
                return;
            }
        }
    }

    // What is to go now. A directory that is sent stays in the register, marked as told: the
    // times it was given are what it is to carry for the life of the mount, and anything that
    // lands under it afterwards, whether a file, a directory or a request of our own, puts it
    // in again with the same times instead of losing them.
    //
    // Deepest first, because setting a directory's times is a write into the directory above
    // it on a store that works the one out from the other. Going up the tree means every
    // directory is set after everything below it has been.
    private List<Ready> Take(bool everything)
    {
        List<Ready> ready = [];

        lock (_sync)
        {
            // A file that is still going up is a directory that is about to be dated by it,
            // so nothing above it has been quiet at all. Not when the mount is coming down:
            // there is nothing to wait for then, and an upload that never ends would keep
            // putting back what the last round has just sent.
            if (!everything && _busy.Count > 0)
            {
                long going = Stopwatch.GetTimestamp();

                foreach (string upload in _busy.Keys)
                {
                    Touch(upload, going);
                }
            }

            foreach (KeyValuePair<string, Waiting> held in _waiting)
            {
                if (held.Value.Sent)
                {
                    continue;
                }

                if (everything || Stopwatch.GetElapsedTime(held.Value.Stamp) >= _quiet)
                {
                    ready.Add(new Ready(held.Key, held.Value.Times));
                }
            }

            foreach (Ready directory in ready)
            {
                Keep(directory.Path, _waiting[directory.Path] with { Sent = true });
            }
        }

        ready.Sort((left, right) => DepthOf(right.Path).CompareTo(DepthOf(left.Path)));

        return ready;
    }

    private static int DepthOf(string path)
    {
        int depth = 0;

        foreach (char letter in path)
        {
            if (letter == '/')
            {
                depth++;
            }
        }

        return depth;
    }

    private static string? ParentOf(string path)
    {
        int slash = path.LastIndexOf('/');

        // The root has nothing above it, and neither has a path that is not one of ours.
        return slash < 0 || path.Length <= 1 ? null : slash == 0 ? "/" : path[..slash];
    }

    private static string Elapsed(long started) =>
        Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString(ElapsedFormat, CultureInfo.InvariantCulture);

    // What a directory is waiting with: the times it was given, when something was last
    // written under it, and whether the store has been told since.
    private readonly record struct Waiting(EntryTimes Times, long Stamp, bool Sent);

    // One directory that is to be set now.
    private readonly record struct Ready(string Path, EntryTimes Times);
}
