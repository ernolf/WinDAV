// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using WinDav.Abstractions;

namespace WinDav.Core.Providers;

/// <summary>
/// A store that holds on to what it has been told about an entry, for a few seconds.
/// </summary>
/// <remarks>
/// <para>
/// Opening a file asks the same question twice: WinFsp asks whether the caller may open the
/// name, and then opens it, milliseconds apart, and each of the two was a request of its own.
/// A listing brings every sibling with it at the price of the one entry that was asked for,
/// because a PROPFIND over a directory costs what a PROPFIND over a single file in it costs.
/// What is kept here is what the server has already said, so that a listing followed by three
/// opens is one request instead of four. See
/// <see href="https://github.com/ernolf/WinDAV/issues/53">#53</see>.
/// </para>
/// <para>
/// The lifetime is counted in seconds and not in minutes: the other writers on the server are
/// the point of the server, and nothing here is told when one of them has written. A lifetime
/// of nothing takes this layer out altogether, which is a request per question and is how a
/// report about a stale directory is narrowed down to the layer that caused it. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#75-the-read-path-read-ahead-keep-attributes-briefly-and-let-the-server-set-the-width">decision 75</see>.
/// </para>
/// <para>
/// This sits under WinFsp's own <c>FileInfoTimeout</c>, which holds what it was handed for a
/// second. It does not replace it and does not change it. Nothing is kept about a path that
/// is not there: a name Windows asks about several times per window and never finds is the
/// one answer that must not be remembered wrongly.
/// </para>
/// <para>
/// The two questions an open asks are milliseconds apart, and what is kept here catches the
/// second one only once the first is answered. Where it is not answered yet, the second waits
/// on the request already in flight instead of sending another, which is
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#79-a-request-that-is-already-in-flight-is-waited-for-instead-of-sent-again">decision 79</see>.
/// </para>
/// </remarks>
public sealed class AttributeCache : IStorageProvider
{
    /// <summary>
    /// How long an entry is held when nobody says otherwise.
    /// </summary>
    /// <remarks>
    /// Long enough that browsing a directory and opening what is in it falls inside one
    /// lifetime, which is what the saving is made of, and short enough that somebody else's
    /// write shows up in the time it takes to look twice. It is the interval the volume's own
    /// figures are already held for.
    /// </remarks>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(10);

    // Ordinal: a store that keeps case has two entries where these differ, and answering the
    // one from the other would hand back a different file than was asked for.
    private readonly ConcurrentDictionary<string, Held> _entries = new(StringComparer.Ordinal);

    // What is on its way, kept apart for the same reason the layer above keeps it apart: an
    // entry and the volume's figures are different questions about the same path.
    private readonly InFlight<RemoteEntry> _asking = new();
    private readonly InFlight<StorageSpace> _measuring = new();

    private readonly IStorageProvider _inner;
    private readonly TimeSpan _lifetime;

    private long _swept = Stopwatch.GetTimestamp();

    /// <summary>
    /// Initialises a new instance of the <see cref="AttributeCache"/> class.
    /// </summary>
    /// <param name="inner">The store the questions go to.</param>
    /// <param name="lifetime">
    /// How long an entry is held, or <see langword="null"/> for
    /// <see cref="DefaultLifetime"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="lifetime"/> is nothing at all, or less. A cache that holds nothing is
    /// a layer that should not have been built; <see cref="Over"/> is what leaves it out.
    /// </exception>
    public AttributeCache(IStorageProvider inner, TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(inner);

        TimeSpan held = lifetime ?? DefaultLifetime;

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(held, TimeSpan.Zero);

        _inner = inner;
        _lifetime = held;
    }

    /// <summary>
    /// Puts a cache over a store, or leaves the store as it is.
    /// </summary>
    /// <param name="provider">The store the questions go to.</param>
    /// <param name="lifetime">
    /// How long an entry is held. Nothing, or less, is the value that switches the whole idea
    /// off, and what comes back then is the store itself with no layer over it at all.
    /// </param>
    /// <returns>The store to ask from here on.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
    public static IStorageProvider Over(IStorageProvider provider, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return lifetime <= TimeSpan.Zero ? provider : new AttributeCache(provider, lifetime);
    }

    /// <inheritdoc/>
    public async Task<DirectoryListing> ListAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        // The listing itself is not held here. Whether it is held at all is the business of
        // the layer underneath, which is the one that can tell whether it still holds; what
        // this one takes from a listing is what it was told about every entry in it.
        DirectoryListing listing = await _inner.ListAsync(path, cancellationToken)
            .ConfigureAwait(false);

        Sweep();

        if (listing.Self is RemoteEntry self)
        {
            Remember(self);
        }

        foreach (RemoteEntry entry in listing.Entries)
        {
            Remember(entry);
        }

        return listing;
    }

    /// <inheritdoc/>
    public async Task<RemoteEntry> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_entries.TryGetValue(path, out Held held))
        {
            if (Fresh(held))
            {
                return held.Entry;
            }

            _entries.TryRemove(new KeyValuePair<string, Held>(path, held));
        }

        // Remembered by the one that fetched, so that everybody who joined does not write
        // down the same answer again.
        return await _asking.JoinAsync(path, () => AskAsync(path), cancellationToken)
            .ConfigureAwait(false);
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
        try
        {
            return await _inner.WriteAsync(path, content, ifMatch, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Also when it failed: a write that reached the server and then broke off has
            // left something there whose length nobody here knows.
            Forget(path);
        }
    }

    /// <inheritdoc/>
    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await _inner.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Forget(path);
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        // Checked here, and in the two verbs below, because these three read a path
        // themselves rather than only handing it to the store, which checks its own.
        ArgumentNullException.ThrowIfNull(path);

        try
        {
            await _inner.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ForgetTree(path);
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
        }
    }

    /// <inheritdoc/>
    public Task<StorageSpace> GetSpaceAsync(string path, CancellationToken cancellationToken = default) =>
        _measuring.JoinAsync(
            path,
            () => _inner.GetSpaceAsync(path, CancellationToken.None),
            cancellationToken);

    // The fetch belongs to none of the callers waiting on it, so none of their tokens reaches
    // it: one that gives up stops waiting and leaves it running for the others.
    private async Task<RemoteEntry> AskAsync(string path)
    {
        RemoteEntry answer = await _inner.GetAsync(path, CancellationToken.None).ConfigureAwait(false);

        Remember(answer);

        return answer;
    }

    private bool Fresh(Held held) => Stopwatch.GetElapsedTime(held.Stamp) < _lifetime;

    private void Remember(RemoteEntry entry) =>
        _entries[entry.Path] = new Held(entry, Stopwatch.GetTimestamp());

    private void Forget(string path) => _entries.TryRemove(path, out _);

    // Everything at a path and below it. A directory that was deleted or moved took what was
    // in it with it, and those entries are held under paths of their own.
    private void ForgetTree(string path)
    {
        Forget(path);

        string below = path.EndsWith('/') ? path : path + '/';

        foreach (string held in _entries.Keys)
        {
            if (held.StartsWith(below, StringComparison.Ordinal))
            {
                _entries.TryRemove(held, out _);
            }
        }
    }

    // What has run out is dropped when it is asked for again, which is enough for a path
    // somebody comes back to and nothing at all for a directory of ten thousand entries that
    // was listed once. So the whole of it is walked, at most once per lifetime.
    private void Sweep()
    {
        long swept = Interlocked.Read(ref _swept);

        if (Stopwatch.GetElapsedTime(swept) < _lifetime)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _swept, Stopwatch.GetTimestamp(), swept) != swept)
        {
            return;
        }

        foreach (KeyValuePair<string, Held> pair in _entries)
        {
            if (!Fresh(pair.Value))
            {
                _entries.TryRemove(pair);
            }
        }
    }

    // A struct: what is held is two words, and there is one of them per entry of a listing.
    private readonly record struct Held(RemoteEntry Entry, long Stamp);
}
