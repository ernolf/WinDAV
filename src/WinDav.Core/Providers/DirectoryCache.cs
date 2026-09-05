// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using WinDav.Abstractions;

namespace WinDav.Core.Providers;

/// <summary>
/// A store that holds the listings it was given, asks one question to find out whether they
/// still hold, and lists the level below one somebody opened.
/// </summary>
/// <remarks>
/// <para>
/// A listing is one request, it costs about 160 milliseconds before it costs anything per
/// entry, and today nothing survives the handle that asked for it: the directory cache of the
/// file system driver hangs on the file node and dies with the last handle rather than with
/// the clock. Two looks at the same directory a fraction of a second apart are two requests.
/// What is kept here is the listing itself, for as long as an attribute is kept, and that is
/// the whole of what a person waits for while browsing.
/// </para>
/// <para>
/// Holding it would be worth nothing on its own, because asking whether a listing still holds
/// costs the one request that fetching it costs. What makes it worth something is that a
/// server gives a directory a version of its own which covers everything underneath it: one
/// listing of an open directory therefore says, of every child directory in it, whether
/// anything anywhere below it has changed. A window standing open on a directory asks about
/// that directory every few seconds anyway, and that question is answered here with the
/// contents rather than without them, so the round costs nothing that was not already spent.
/// </para>
/// <para>
/// A listing that is held is also read for what is not in it. A name absent from a listing
/// that still holds is absent on the server, and saying so costs nothing where saying
/// anything else costs a request. That is not a small part of the traffic: over an evening
/// at a live mount, 2304 of 5458 requests were lookups of names that do not exist, asked by
/// programs that watch every folder a window shows. The listing that answers need not be the
/// one directly around the name: a whole path arrives at once, and the nearest listing above
/// it settles it, because a directory that is not there has nothing under it. Where that
/// listing has run out it is fetched again rather than stepped over: these names arrive in
/// bursts far shorter than a listing lives, so one listing answers a whole burst that would
/// otherwise be one request per name.
/// </para>
/// <para>
/// A question about a single name is settled here and never on the wire. Where nothing held
/// settles it, the directory around the name is listed and the answer comes out of that
/// listing: it is the same one request that a question about the name would have been, and it
/// answers every other name in that directory for as long as it is held. Over two runs at a
/// live mount, questions about single names were 82 of 289 requests and 126 of 235; under the
/// rule they are none, and the total falls by a fifth and by a third. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#80-a-question-about-a-single-name-is-settled-inside-the-mount-never-on-the-wire">decision 80</see>.
/// </para>
/// <para>
/// A name that has been looked for in other directories and found in none of them buys no
/// listing. Over four runs at a live mount, 269 of 2199 questions about a single name fell
/// in a directory nothing had listed, and not one of them named something that was there;
/// the names behind them were the same few, asked for in directory after directory and
/// present in none. Such a name is answered as absent and no listing is bought to say so,
/// which takes 247 of those 269 requests off the wire. A directory that is held answers out
/// of its listing whatever the name is. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#81-a-name-not-found-elsewhere-buys-no-listing">decision 81</see>.
/// </para>
/// <para>
/// A version missing on either side vouches for nothing and is believed as nothing: what is
/// held then ages out by itself, which is the behaviour of a store that has no versions for
/// directories at all, and it is never wrong. Nothing here is written to disk, and holding
/// nothing is one of the settings. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#76-listings-are-kept-an-etag-says-whether-they-still-hold-and-f5-throws-them-away">decision 76</see>,
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#77-a-listing-that-is-held-answers-what-is-not-in-it">decision 77</see>
/// and
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#78-a-listing-that-has-run-out-is-fetched-again-when-a-name-in-it-is-asked-for">decision 78</see>.
/// </para>
/// <para>
/// Two callers that want the same listing at the same moment share the one request rather
/// than sending it twice, the reader who has caught up with what is being read ahead for him
/// among them, which is
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#79-a-request-that-is-already-in-flight-is-waited-for-instead-of-sent-again">decision 79</see>.
/// </para>
/// </remarks>
public sealed class DirectoryCache : IStorageProvider
{
    // How many rounds' worth of directories may be waiting at once. Not a setting: it says
    // how far back a reader can still turn round, which is a property of reading and not of
    // a server, and the width of a round is already his to set.
    private const int Rounds = 4;

    // Ordinal, for the reason the attribute cache is ordinal: a store that keeps case has two
    // directories where these differ.
    private readonly ConcurrentDictionary<string, Held> _listings = new(StringComparer.Ordinal);

    // The directories a name was asked for in and not found in, and the names that have
    // reached the threshold. Ordinal for the reason the listings are ordinal: a store that
    // keeps case has two names where these differ. The directories of a name are let go of
    // once it is burned, because nothing is counted about it after that.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _nowhere
        = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, byte> _burned = new(StringComparer.Ordinal);

    // What a name that is not there has cost: how often it was answered as absent, and how
    // many listings those answers bought. Only names that turned out absent are in here, so
    // it holds what _nowhere and _burned already hold and nothing besides.
    private readonly ConcurrentDictionary<string, int> _asked = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _bought = new(StringComparer.Ordinal);

    // What is on its way. A caller that finds a fetch for its path already running waits
    // on that one instead of sending the same request again; the two are apart because a
    // listing and the volume's figures are different questions about the same path.
    private readonly InFlight<DirectoryListing> _fetching = new();
    private readonly InFlight<StorageSpace> _measuring = new();

    // What is waiting to be read ahead, the newest round at the front. A round is not taken
    // away when the next one begins: it is pushed back. Anything that walks a drive
    // enumerates as it goes, and every enumeration begins a round, so a round that emptied
    // the queue would leave whoever is reading with nothing read ahead exactly while he
    // reads. What falls off is the back, which is for directories he passed rounds ago.
    private readonly LinkedList<Wanted> _queue = new();

    // Held while the queue is read or rearranged. Nothing waits on a request inside it.
    private readonly Lock _queued = new();

    // Held while what is kept is rearranged. Nothing waits on a request inside it: a listing
    // has arrived by the time the lock is taken.
    private readonly Lock _sync = new();

    private readonly IStorageProvider _inner;
    private readonly TimeSpan _lifetime;
    private readonly DirectorySettings _settings;
    private readonly RequestGate _gate;
    private readonly CancellationToken _stopping;

    private int _budget;
    private int _pumping;

    /// <summary>
    /// Initialises a new instance of the <see cref="DirectoryCache"/> class.
    /// </summary>
    /// <param name="inner">The store the questions go to.</param>
    /// <param name="lifetime">
    /// How long a listing is answered without asking. It is the interval the whole idea runs
    /// at, and it is the one an attribute is held for, because it is the same round trip that
    /// carries both.
    /// </param>
    /// <param name="settings">
    /// How far ahead to list, how much of that at a time, and how many listings to hold, or
    /// <see langword="null"/> for what was measured.
    /// </param>
    /// <param name="gate">
    /// The one that says how many requests this mount may have on the wire. Listing ahead
    /// happens behind whatever a person is waiting for, so it asks the same gate for room.
    /// </param>
    /// <param name="stopping">Ends the listing ahead; the mount coming down is what ends it.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="lifetime"/> is nothing at all, or less. A store of listings that holds
    /// nothing is a layer that should not have been built; <see cref="Over"/> leaves it out.
    /// </exception>
    public DirectoryCache(
        IStorageProvider inner,
        TimeSpan lifetime,
        DirectorySettings? settings,
        RequestGate gate,
        CancellationToken stopping = default)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        _inner = inner;
        _lifetime = lifetime;
        _settings = settings ?? new DirectorySettings();
        _gate = gate;
        _stopping = stopping;
    }

    /// <summary>
    /// Puts a store of listings over a store, or leaves the store as it is.
    /// </summary>
    /// <param name="provider">The store the questions go to.</param>
    /// <param name="lifetime">
    /// How long a listing is answered without asking. Nothing, or less, switches the whole
    /// idea off, and so does a ceiling of no directories at all.
    /// </param>
    /// <param name="settings">How far ahead to list, and how much to hold.</param>
    /// <param name="gate">The one that says how many requests may be on the wire.</param>
    /// <param name="stopping">Ends the listing ahead.</param>
    /// <returns>The store to ask from here on.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="provider"/> or <paramref name="gate"/> is null.
    /// </exception>
    public static IStorageProvider Over(
        IStorageProvider provider,
        TimeSpan lifetime,
        DirectorySettings? settings,
        RequestGate gate,
        CancellationToken stopping = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        DirectorySettings asked = settings ?? new DirectorySettings();

        return lifetime <= TimeSpan.Zero || asked.Directories <= 0
            ? provider
            : new DirectoryCache(provider, lifetime, asked, gate, stopping);
    }

    /// <inheritdoc/>
    public async Task<DirectoryListing> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        // A round belongs to the listing somebody waited for, and enumerating a directory is
        // the one thing that begins one. It goes in front of whatever is still waiting rather
        // than in place of it: what this round queues is where the reader is now, and what was
        // queued before it is where he was, which is worth a request only once this round has
        // had its own.
        Volatile.Write(ref _budget, _settings.Requests);

        // Held, so nothing is sent for it, and the level below it is queued all the same.
        // Reading ahead runs one level ahead of the reader and not one level ahead of the
        // wire: a directory somebody opens is one he is about to go into, whether or not the
        // listing had to be fetched. Without this a round ends where it succeeded, because
        // the directory it read ahead is answered from what is held and queues nothing.
        if (Current(path) is DirectoryListing current)
        {
            Queue(current.Entries, _settings.Depth);

            return current;
        }

        return await FetchAsync(path, _settings.Depth, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<RemoteEntry> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        // A directory this has listed before is asked about with its contents rather than
        // without them. That is the round: one request answers what the directory is, and the
        // version it carries for every child directory says which of the listings held below
        // it still hold.
        if (_listings.ContainsKey(path))
        {
            DirectoryListing listing = await CurrentAsync(path, cancellationToken).ConfigureAwait(false);

            if (listing.Self is RemoteEntry self)
            {
                return self;
            }
        }

        // What says a directory holds these entries says as plainly that it holds no others.
        // The nearest listing that is held answers this, and one that has run out is fetched
        // again before it does: three times the bytes for a ninth of the round trips, counted
        // against a live mount.
        if (await MissingAsync(path, cancellationToken).ConfigureAwait(false))
        {
            Nowhere(path);

            throw Absent(path, bought: false);
        }

        // Nothing held settles the name, so the directory around it is listed and the answer
        // is read out of that listing. One request either way, and where a question about the
        // name would have bought that one answer once, the listing answers every name in that
        // directory for as long as it holds. Unless the name is one that has been looked for
        // in other directories and found in none of them, which is what a probe asks for:
        // that one is absent, and no listing is bought to say so.
        if (ParentOf(path) is string around)
        {
            bool bought = false;

            if (Current(around) is not DirectoryListing listing)
            {
                if (Burned(path))
                {
                    throw Absent(path, bought: false);
                }

                // Listed at depth nothing: nobody opened that directory, somebody asked about
                // one name in it, and what is read ahead belongs to a directory a person is
                // looking at.
                listing = await FetchAsync(around, 0, cancellationToken).ConfigureAwait(false);
                bought = true;
            }

            if (Find(listing.Entries, path) is RemoteEntry entry)
            {
                return entry;
            }

            Nowhere(path);

            throw Absent(path, bought);
        }

        // The root, which has no directory around it to be listed instead.
        return await _inner.GetAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<Stream> OpenReadAsync(
        string path,
        long offset = 0,
        long? count = null,
        CancellationToken cancellationToken = default) =>
        _inner.OpenReadAsync(path, offset, count, cancellationToken);

    /// <inheritdoc/>
    public async Task<string?> WriteAsync(
        string path,
        Stream content,
        string? ifMatch = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        try
        {
            return await _inner.WriteAsync(path, content, ifMatch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The directory this is in shows a length and a time that have just changed, and
            // a file that was not there before is in it now.
            ForgetParent(path);
        }
    }

    /// <inheritdoc/>
    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        try
        {
            await _inner.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ForgetParent(path);
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        try
        {
            await _inner.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ForgetTree(path);
            ForgetParent(path);
        }
    }

    /// <inheritdoc/>
    public async Task MoveAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(destinationPath);

        try
        {
            await _inner.MoveAsync(sourcePath, destinationPath, overwrite, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ForgetTree(sourcePath);
            ForgetTree(destinationPath);
            ForgetParent(sourcePath);
            ForgetParent(destinationPath);
        }
    }

    /// <inheritdoc/>
    public async Task CopyAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(destinationPath);

        try
        {
            await _inner.CopyAsync(sourcePath, destinationPath, overwrite, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // A copy leaves the source as it was; what is at the destination is new.
            ForgetTree(destinationPath);
            ForgetParent(destinationPath);
        }
    }

    /// <inheritdoc/>
    public Task<StorageSpace> GetSpaceAsync(string path, CancellationToken cancellationToken = default) =>
        _measuring.JoinAsync(path, () => _inner.GetSpaceAsync(path, _stopping), cancellationToken);

    /// <summary>
    /// The names that were asked for on this mount and were in no directory they were asked
    /// for in, the one that cost the most listings first.
    /// </summary>
    /// <returns>What is counted at this moment. A mount that is still running keeps adding.</returns>
    public IReadOnlyList<AbsentName> Absences()
    {
        List<AbsentName> absent = new(_asked.Count);

        foreach (KeyValuePair<string, int> name in _asked)
        {
            _bought.TryGetValue(name.Key, out int listings);

            absent.Add(new AbsentName(name.Key, name.Value, listings));
        }

        absent.Sort(static (left, right) =>
        {
            int order = right.Listings.CompareTo(left.Listings);

            if (order != 0)
            {
                return order;
            }

            order = right.Asked.CompareTo(left.Asked);

            return order != 0 ? order : string.CompareOrdinal(left.Name, right.Name);
        });

        return absent;
    }

    private static string? ParentOf(string path)
    {
        int slash = path.LastIndexOf('/');

        // The root has nothing above it, and neither has a path that is not one of ours.
        return slash < 0 || path.Length <= 1 ? null : slash == 0 ? "/" : path[..slash];
    }

    // Ordinal, because the store keeps case: a listing that holds one spelling is not a
    // listing that holds another.
    private static RemoteEntry? Find(IReadOnlyList<RemoteEntry> entries, string path)
    {
        foreach (RemoteEntry entry in entries)
        {
            if (string.Equals(entry.Path, path, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    // The file system is handed a whole path at once rather than a component at a time, so
    // the directory a name is in is often one nothing has ever listed while a directory
    // above it is held. The nearest listing that is held decides: it says whether the one
    // step below it is there at all, and a step that is not there takes everything under it
    // with it. Where that step is there, nothing above says anything about the names further
    // down, and the question goes to the server as before.
    private async Task<bool> MissingAsync(string path, CancellationToken cancellationToken)
    {
        string child = path;

        for (string? above = ParentOf(path); above is not null; above = ParentOf(above))
        {
            if (_listings.TryGetValue(above, out Held held))
            {
                // Held, but run out. Fetched again rather than stepped over: these names
                // arrive in bursts far shorter than a listing lives, because what asks is
                // walking a path upwards, so the one listing answers the whole burst where
                // the burst is otherwise one request per name.
                if (!Fresh(held))
                {
                    DirectoryListing listing = await CurrentAsync(above, cancellationToken)
                        .ConfigureAwait(false);

                    return Find(listing.Entries, child) is null;
                }

                return Find(held.Entries, child) is null;
            }

            // Nothing written down, but a listing of this directory is on its way. What it
            // will say is the answer, and waiting for it is a request that is not sent: a
            // listing is written down after it has come back and been parsed, and these
            // questions arrive in that window.
            if (_fetching.Joined(above, cancellationToken) is Task<DirectoryListing> running)
            {
                DirectoryListing listing = await running.ConfigureAwait(false);

                return Find(listing.Entries, child) is null;
            }

            child = above;
        }

        return false;
    }

    private bool Fresh(Held held) => Stopwatch.GetElapsedTime(held.Stamp) < _lifetime;

    // What is held of a directory, where it is held and still holds.
    private DirectoryListing? Current(string path) =>
        _listings.TryGetValue(path, out Held held) && Fresh(held)
            ? new DirectoryListing(held.Entries, held.Self)
            : null;

    // The name at the end of a path. What is counted about a probe is the name and never the
    // path, because the same name is what arrives in directory after directory.
    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private bool Burned(string path) =>
        _settings.Probes > 0 && _burned.ContainsKey(NameOf(path));

    // The answer that a name is not there, and what that answer cost. A listing counts as
    // bought only where it was fetched to give this one answer; one that was already held,
    // and one the name was burned before, cost nothing.
    private ProviderException Absent(string path, bool bought)
    {
        string name = NameOf(path);

        _asked.AddOrUpdate(name, 1, static (_, asked) => asked + 1);

        if (bought)
        {
            _bought.AddOrUpdate(name, 1, static (_, listings) => listings + 1);
        }

        return new ProviderException(ProviderError.NotFound, $"There is nothing at '{path}'.");
    }

    // One more directory a name was not in, and the name is burned once there are enough of
    // them. Only where a name was absent is counted; one that was found is not counted at all.
    private void Nowhere(string path)
    {
        if (_settings.Probes <= 0 || ParentOf(path) is not string around)
        {
            return;
        }

        string name = NameOf(path);

        if (_burned.ContainsKey(name))
        {
            return;
        }

        ConcurrentDictionary<string, byte> directories = _nowhere.GetOrAdd(
            name,
            _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));

        directories[around] = 0;

        if (directories.Count < _settings.Probes)
        {
            return;
        }

        _burned[name] = 0;
        _nowhere.TryRemove(name, out _);
    }

    // The listing of a directory as it stands, fetched again where what is held has run out.
    // No round behind it: this answers whoever is asking what a directory is rather than
    // opening it, and anything that walks a drive asks that of every directory it passes. A
    // round started there reads ahead of a walker instead of a reader, one level for every
    // directory it touches, and each of those rounds arms the next.
    private async Task<DirectoryListing> CurrentAsync(string path, CancellationToken cancellationToken) =>
        Current(path) is DirectoryListing current
            ? current
            : await FetchAsync(path, 0, cancellationToken).ConfigureAwait(false);

    // Every fetch goes through here, the one somebody is waiting for and the one read ahead
    // alike: the two meet on a directory the reader has reached first, and one of them is a
    // request that need not be sent.
    private async Task<DirectoryListing> FetchAsync(string path, int depth, CancellationToken cancellationToken)
    {
        DirectoryListing listing = await _fetching
            .JoinAsync(path, () => ReadAsync(path), cancellationToken)
            .ConfigureAwait(false);

        // Outside the join, with the depth of whoever asked: what a caller wants read ahead
        // is not what the caller it joined wants, and the one that joined a shallower fetch
        // would otherwise get nothing read ahead at all.
        Queue(listing.Entries, depth);

        return listing;
    }

    // The fetch is the layer's rather than any one caller's, so no caller's token reaches it:
    // one that gives up stops waiting and leaves it running for whoever else has joined. What
    // ends it is the mount.
    private async Task<DirectoryListing> ReadAsync(string path)
    {
        DirectoryListing listing = await _inner.ListAsync(path, _stopping).ConfigureAwait(false);

        lock (_sync)
        {
            // Before it is replaced: what says whether a child listing still holds is the
            // version this directory carried for that child the last time it was asked.
            Vouch(path, listing.Entries);

            _listings[path] = new Held(listing.Entries, listing.Self, Stopwatch.GetTimestamp());

            Trim();
        }

        return listing;
    }

    // Called under the lock. A child directory whose version is the one it had is current
    // through and through, because a version covers everything below it; one whose version
    // has changed is stale somewhere below and there is no telling where. A version missing
    // on either side says nothing, and nothing is done about it: what is held ages out by
    // itself.
    private void Vouch(string path, IReadOnlyList<RemoteEntry> fresh)
    {
        if (!_listings.TryGetValue(path, out Held previous))
        {
            return;
        }

        Dictionary<string, string?> before = new(StringComparer.Ordinal);

        foreach (RemoteEntry entry in previous.Entries)
        {
            if (entry.IsDirectory)
            {
                before[entry.Path] = entry.ETag;
            }
        }

        long now = Stopwatch.GetTimestamp();

        foreach (RemoteEntry entry in fresh)
        {
            if (!entry.IsDirectory
                || !before.TryGetValue(entry.Path, out string? was)
                || was is null
                || entry.ETag is null)
            {
                continue;
            }

            if (string.Equals(was, entry.ETag, StringComparison.Ordinal))
            {
                Restamp(entry.Path, now);
            }
            else
            {
                Drop(entry.Path);
            }
        }
    }

    // Called under the lock, over a directory and everything held below it.
    private void Restamp(string path, long stamp)
    {
        foreach (string key in Tree(path))
        {
            if (_listings.TryGetValue(key, out Held held))
            {
                _listings[key] = held with { Stamp = stamp };
            }
        }
    }

    // Called under the lock.
    private void Drop(string path)
    {
        foreach (string key in Tree(path))
        {
            _listings.TryRemove(key, out _);
        }
    }

    // Called under the lock. What has gone longest without being proven current goes first:
    // it is the one that would have been asked for again soonest anyway.
    private void Trim()
    {
        int over = _listings.Count - _settings.Directories;

        if (over <= 0)
        {
            return;
        }

        foreach (KeyValuePair<string, Held> pair in _listings.OrderBy(pair => pair.Value.Stamp).Take(over))
        {
            _listings.TryRemove(pair);
        }
    }

    private List<string> Tree(string path)
    {
        List<string> keys = [path];

        string below = path.EndsWith('/') ? path : path + '/';

        foreach (string key in _listings.Keys)
        {
            if (key.StartsWith(below, StringComparison.Ordinal))
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    private void ForgetParent(string path)
    {
        if (ParentOf(path) is not string parent)
        {
            return;
        }

        lock (_sync)
        {
            _listings.TryRemove(parent, out _);
        }
    }

    private void ForgetTree(string path)
    {
        lock (_sync)
        {
            Drop(path);
        }
    }

    private void Queue(IReadOnlyList<RemoteEntry> entries, int depth)
    {
        if (depth <= 0 || Volatile.Read(ref _budget) <= 0 || _stopping.IsCancellationRequested)
        {
            return;
        }

        List<RemoteEntry> wanted = [];

        foreach (RemoteEntry entry in entries)
        {
            if (!entry.IsDirectory || (_listings.TryGetValue(entry.Path, out Held held) && Fresh(held)))
            {
                continue;
            }

            wanted.Add(entry);
        }

        if (wanted.Count == 0)
        {
            return;
        }

        // Newest first. A round is a few seconds long at two requests in flight, and what it
        // does not reach is dropped rather than carried over, so the order decides both which
        // directories the round gets to and which of them arrive before the person does. A
        // name says nothing about who opens it next; the time of the last change says
        // something, and it came with the listing at no cost. Only the children of one
        // directory are ever compared, which is what makes a server that carries a change
        // upwards the right answer here rather than the wrong one. The order is stable, so
        // equal times keep the order the server gave, and a child the store gave no time for
        // goes last rather than out: not every store fills it in.
        Push(
        [
            .. wanted
                .OrderByDescending(directory => directory.LastModified ?? DateTimeOffset.MinValue)
                .Select(directory => new Wanted(directory.Path, depth - 1)),
        ]);

        Pump();
    }

    private void Pump()
    {
        // One at a time. Whoever finds it running has queued its work for the one that is,
        // and the loop below picks up what arrives while it runs.
        if (Interlocked.CompareExchange(ref _pumping, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(PumpAsync, CancellationToken.None);
    }

    private async Task PumpAsync()
    {
        bool again;

        do
        {
            try
            {
                await DrainAsync().ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _pumping, 0);
            }

            // Something may have arrived between the queue running dry and the flag going
            // down, and whoever queued it saw the flag up and started nothing.
            again = Waiting() && Interlocked.CompareExchange(ref _pumping, 1, 0) == 0;
        }
        while (again);
    }

    private async Task DrainAsync()
    {
        while (!_stopping.IsCancellationRequested && Take(out Wanted wanted))
        {
            if (Interlocked.Decrement(ref _budget) < 0)
            {
                Empty();

                return;
            }

            if (_listings.TryGetValue(wanted.Path, out Held held) && Fresh(held))
            {
                continue;
            }

            bool refused = false;

            _gate.Enter();

            try
            {
                await FetchAsync(wanted.Path, wanted.Depth, _stopping).ConfigureAwait(false);
            }
            catch (ProviderException failure)
            {
                // Busy is the one answer worth reacting to, and the answer to it is to stop
                // asking for what nobody has asked for yet. Anything else is about the one
                // directory: it is not held, and whoever opens it fetches it themselves.
                refused = failure.Error == ProviderError.Busy;

                if (refused)
                {
                    Empty();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _gate.Leave(refused);
            }

            if (refused)
            {
                return;
            }
        }
    }

    // A round to the front, in the order it was sorted into.
    private void Push(IReadOnlyList<Wanted> round)
    {
        lock (_queued)
        {
            for (int index = round.Count - 1; index >= 0; index--)
            {
                _queue.AddFirst(round[index]);
            }

            // Something has to fall off, or a drive being walked queues faster than the
            // requests can be spent and the queue grows for as long as the walk lasts. The
            // back is what falls off: a few rounds' worth is as far back as a reader can
            // still turn round, and behind that is where he no longer is.
            int held = _settings.Requests * Rounds;

            while (_queue.Count > held)
            {
                _queue.RemoveLast();
            }
        }
    }

    private bool Waiting()
    {
        lock (_queued)
        {
            return _queue.Count > 0;
        }
    }

    private bool Take(out Wanted wanted)
    {
        lock (_queued)
        {
            LinkedListNode<Wanted>? first = _queue.First;

            if (first is null)
            {
                wanted = default;

                return false;
            }

            _queue.RemoveFirst();

            wanted = first.Value;

            return true;
        }
    }

    private void Empty()
    {
        lock (_queued)
        {
            _queue.Clear();
        }
    }

    // What is held of one directory: what was in it, what the store said about it, and when
    // that was last proven current.
    private readonly record struct Held(IReadOnlyList<RemoteEntry> Entries, RemoteEntry? Self, long Stamp);

    // A directory to list before anybody asks for it, and how many levels below it to go on.
    private readonly record struct Wanted(string Path, int Depth);
}
