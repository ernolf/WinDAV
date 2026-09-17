// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using WinDav.Abstractions;

namespace WinDav.Fs.Tests;

// A store held in memory, with the same seam a real provider has: paths with slashes, and
// a ProviderException for everything that cannot be done. Only what this cut of the file
// system reaches is implemented; copying a directory throws, so a test that reached it by
// accident fails loudly instead of passing quietly.
internal sealed class FakeStore : IStorageProvider
{
    private readonly Dictionary<string, RemoteEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _content = new(StringComparer.OrdinalIgnoreCase);

    private int _version;

    private int _piecesSent;

    private int _piecesRefused;

    private int _abandoned;

    public FakeStore()
    {
        AddDirectory("/");
    }

    // What the file system asked to read, in the store's own spelling.
    public List<string> Opened { get; } = [];

    // Every range that was asked for, in order. One entry is one request, which is what a
    // test about the read path counts.
    public List<(long Offset, long? Count)> Reads { get; } = [];

    // What was written, in order: where it went, the bytes, the entity tag the write was
    // made conditional on, the times it was to carry, and whether it was the write that had
    // to make the name. One entry is one upload.
    public List<(string Path, byte[] Content, string? IfMatch, EntryTimes Times, bool MustBeNew)> Writes { get; } = [];

    // Every time the times were set on their own, in order. One entry is one request that
    // would not have been made had they travelled with an upload.
    public List<(string Path, EntryTimes Times)> TimesSet { get; } = [];

    public long LastOffset { get; private set; }

    public long? LastCount { get; private set; }

    // Set to make every call fail from then on, which is how a server going away mid-read
    // is arranged.
    public ProviderError? FailWith { get; set; }

    // What the store says about its room. Nothing by default, which is a store that keeps no
    // such figure.
    public StorageSpace Space { get; set; } = StorageSpace.Unknown;

    // Set to take a file in pieces of this size, the way a store that chunks does. Nothing by
    // default, which is a store that takes a file only whole.
    public int? PieceSize { get; set; }

    // Set to size every piece after the first differently, the way a store that sizes its
    // pieces by how long the first one took does.
    public int? LaterPieceSize { get; set; }

    // How many pieces the store takes on their way at once. One by default.
    public int PiecesAtOnce { get; set; } = 1;

    // Set to refuse every piece, the way a store that has run out of room does.
    public bool RefusePieces { get; set; }

    // Set to keep every piece waiting until it completes, the way a store with all of its
    // pieces still on their way does.
    public Task? PiecesHeld { get; set; }

    // Set to take every piece at once and read it afterwards, the way a store with room for
    // all of its pieces on their way does.
    public bool PiecesTravel { get; set; }

    // The pieces taken and refused, and the uploads let go unfinished. Counted on the thread
    // that sends ahead, so read with a barrier.
    public int PiecesSent => Volatile.Read(ref _piecesSent);

    public int PiecesRefused => Volatile.Read(ref _piecesRefused);

    public int Abandoned => Volatile.Read(ref _abandoned);

    // Where a file was put together out of pieces, in order. One entry is one upload that did
    // not go whole.
    public List<string> Assembled { get; } = [];

    public void AddDirectory(string path) => _entries[path] = new RemoteEntry(path, true);

    public void AddFile(
        string path,
        string content,
        EntryPermissions? permissions = null,
        DateTimeOffset? lastModified = null,
        string? eTag = null)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);

        _entries[path] = new RemoteEntry(path, false)
        {
            Length = bytes.Length,
            Permissions = permissions,
            LastModified = lastModified,
            ETag = eTag,
        };

        _content[path] = bytes;
    }

    // A file worth reading in pieces. Every byte says where it is, so a test can tell a
    // window served from the wrong place from one served from the right one.
    public byte[] AddFileOfSize(string path, int length)
    {
        byte[] bytes = new byte[length];

        for (int index = 0; index < length; index++)
        {
            bytes[index] = (byte)(index % 251);
        }

        _entries[path] = new RemoteEntry(path, false) { Length = length };
        _content[path] = bytes;

        return bytes;
    }

    // A file the store lists without saying how long it is, which a WebDAV server is
    // entitled to do.
    public void AddFileOfUnknownLength(string path, string content)
    {
        _entries[path] = new RemoteEntry(path, false);
        _content[path] = Encoding.UTF8.GetBytes(content);
    }

    public Task<DirectoryListing> ListAsync(string path, CancellationToken cancellationToken)
    {
        Fail();

        RemoteEntry directory = Find(path);

        if (!directory.IsDirectory)
        {
            throw new ProviderException(ProviderError.Conflict);
        }

        List<RemoteEntry> children = [];

        foreach (KeyValuePair<string, RemoteEntry> pair in _entries)
        {
            bool self = string.Equals(pair.Key, path, StringComparison.OrdinalIgnoreCase);

            if (!self && string.Equals(ParentOf(pair.Key), path, StringComparison.OrdinalIgnoreCase))
            {
                children.Add(pair.Value);
            }
        }

        return Task.FromResult(new DirectoryListing(children, directory));
    }

    public Task<RemoteEntry> GetAsync(string path, CancellationToken cancellationToken)
    {
        Fail();

        return Task.FromResult(Find(path));
    }

    public Task<StorageSpace> GetSpaceAsync(string path, CancellationToken cancellationToken)
    {
        Fail();

        return Task.FromResult(Space);
    }

    public Task<Stream> OpenReadAsync(string path, long offset, long? count, CancellationToken cancellationToken)
    {
        Fail();

        Opened.Add(path);
        Reads.Add((offset, count));
        LastOffset = offset;
        LastCount = count;

        if (!_content.TryGetValue(path, out byte[]? bytes))
        {
            throw new ProviderException(ProviderError.NotFound);
        }

        int start = (int)Math.Min(offset, bytes.Length);
        int length = bytes.Length - start;

        if (count is long wanted && wanted < length)
        {
            length = (int)wanted;
        }

        return Task.FromResult<Stream>(new MemoryStream(bytes, start, length, false));
    }

    public Task<string?> CreateFileAsync(string path, CancellationToken cancellationToken)
    {
        Fail();

        if (_entries.ContainsKey(path))
        {
            throw new ProviderException(ProviderError.AlreadyExists);
        }

        if (!_entries.ContainsKey(ParentOf(path)))
        {
            throw new ProviderException(ProviderError.Conflict);
        }

        return Task.FromResult<string?>(Store(path, []));
    }

    public async Task<string?> WriteAsync(
        string path,
        Stream content,
        string? ifMatch,
        EntryTimes times,
        bool mustBeNew,
        CancellationToken cancellationToken)
    {
        Fail();
        Guard(path, ifMatch, mustBeNew);

        using MemoryStream taken = new();

        await content.CopyToAsync(taken, cancellationToken).ConfigureAwait(false);

        byte[] bytes = taken.ToArray();

        Writes.Add((path, bytes, ifMatch, times, mustBeNew));

        // Like a store that takes the times on the request that writes the file: no second
        // request, and the entity tag it answers with is still good afterwards.
        return Store(path, bytes, times);
    }

    public IUpload? BeginUpload(string path) => PieceSize is int size ? new FakeUpload(this, path, size) : null;

    public Task SetTimesAsync(string path, EntryTimes times, CancellationToken cancellationToken)
    {
        Fail();

        RemoteEntry entry = Find(path);

        TimesSet.Add((path, times));

        _entries[path] = Timed(entry, times);

        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        Fail();

        if (_entries.ContainsKey(path))
        {
            throw new ProviderException(ProviderError.AlreadyExists);
        }

        if (!_entries.ContainsKey(ParentOf(path)))
        {
            throw new ProviderException(ProviderError.Conflict);
        }

        AddDirectory(path);

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        Fail();

        if (!_entries.ContainsKey(path))
        {
            throw new ProviderException(ProviderError.NotFound);
        }

        // With everything under it, the way one request on a directory goes out.
        foreach (string key in Under(path))
        {
            _entries.Remove(key);
            _content.Remove(key);
        }

        return Task.CompletedTask;
    }

    public Task MoveAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        Fail();

        if (!_entries.ContainsKey(sourcePath))
        {
            throw new ProviderException(ProviderError.NotFound);
        }

        if (_entries.ContainsKey(destinationPath))
        {
            if (!overwrite)
            {
                throw new ProviderException(ProviderError.AlreadyExists);
            }

            _entries.Remove(destinationPath);
            _content.Remove(destinationPath);
        }

        foreach (string key in Under(sourcePath))
        {
            RemoteEntry moved = _entries[key];
            string path = destinationPath + key[sourcePath.Length..];

            _entries.Remove(key);
            _entries[path] = new RemoteEntry(path, moved.IsDirectory)
            {
                Length = moved.Length,
                Permissions = moved.Permissions,
                LastModified = moved.LastModified,
            };

            if (_content.Remove(key, out byte[]? bytes))
            {
                _content[path] = bytes;
            }
        }

        return Task.CompletedTask;
    }

    public Task CopyAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    // What the store has at a path, or nothing, which is how a test asks whether an entry
    // is there without going through the file system.
    public RemoteEntry? At(string path) => _entries.GetValueOrDefault(path);

    // What the store now holds at a path, which is how a test asks what an upload put there.
    public string ContentOf(string path) => Encoding.UTF8.GetString(_content[path]);

    // An entry and everything below it.
    private List<string> Under(string path)
    {
        string prefix = path.EndsWith('/') ? path : path + "/";

        return
        [
            .. _entries.Keys.Where(key =>
                string.Equals(key, path, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)),
        ];
    }

    private static string ParentOf(string path)
    {
        int slash = path.LastIndexOf('/');

        return slash <= 0 ? "/" : path[..slash];
    }

    // What a store does on every write: the entry is what it now is, and it carries a tag
    // that stands for this version and no other.
    // The entry as it is once the times it was given have been written into it, leaving
    // whatever was already there where a time was not named.
    private static RemoteEntry Timed(RemoteEntry entry, EntryTimes times) =>
        new(entry.Path, entry.IsDirectory)
        {
            Length = entry.Length,
            Id = entry.Id,
            Permissions = entry.Permissions,
            ETag = entry.ETag,
            Created = times.Created ?? entry.Created,
            LastModified = times.LastModified ?? entry.LastModified,
        };

    private string Store(string path, byte[] bytes, EntryTimes times = default)
    {
        string eTag = $"v{++_version}";

        _entries[path] = new RemoteEntry(path, false)
        {
            Length = bytes.Length,
            ETag = eTag,
            Created = times.Created,
            LastModified = times.LastModified,
        };

        _content[path] = bytes;

        return eTag;
    }

    // What a store checks before it writes: the version the write was made conditional on,
    // and whether the name has to be free.
    private void Guard(string path, string? ifMatch, bool mustBeNew)
    {
        if (ifMatch is not null && !string.Equals(_entries.GetValueOrDefault(path)?.ETag, ifMatch, StringComparison.Ordinal))
        {
            throw new ProviderException(ProviderError.PreconditionFailed);
        }

        if (mustBeNew && _entries.ContainsKey(path))
        {
            throw new ProviderException(ProviderError.AlreadyExists);
        }
    }

    private RemoteEntry Find(string path)
    {
        if (_entries.TryGetValue(path, out RemoteEntry? entry))
        {
            return entry;
        }

        throw new ProviderException(ProviderError.NotFound, $"Nothing at {path}.");
    }

    private void Fail()
    {
        if (FailWith is ProviderError error)
        {
            throw new ProviderException(error);
        }
    }

    private void CountSent() => Interlocked.Increment(ref _piecesSent);

    private void CountRefused() => Interlocked.Increment(ref _piecesRefused);

    private void CountAbandoned() => Interlocked.Increment(ref _abandoned);

    // An upload in pieces that keeps them until the finish puts the file together, which is
    // when the entry changes and not before.
    private sealed class FakeUpload(FakeStore store, string path, int pieceSize) : IUpload
    {
        // One entry per piece, in the order they were sent, filled in once a piece is read,
        // which for a piece that travels is on another thread.
        private readonly List<byte[]> _pieces = [];

        private readonly Lock _gate = new();

        private readonly List<Task> _travelling = [];

        private bool _finished;

        public Task<long> GetPieceSizeAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return Task.FromResult<long>(_pieces.Count > 0 && store.LaterPieceSize is int later ? later : pieceSize);
            }
        }

        public Task<int> GetPiecesAtOnceAsync(CancellationToken cancellationToken) => Task.FromResult(store.PiecesAtOnce);

        // The piece is the upload's once handed over, taken or refused.
        public async Task<bool> SendAsync(Stream piece, CancellationToken cancellationToken)
        {
            int number;

            lock (_gate)
            {
                number = _pieces.Count;
                _pieces.Add([]);
            }

            if (store.PiecesTravel)
            {
                _travelling.Add(TakeAsync(piece, number));

                return true;
            }

            await TakeAsync(piece, number).ConfigureAwait(false);

            return true;
        }

        public async Task<string?> FinishAsync(
            Stream rest,
            string? ifMatch,
            EntryTimes times,
            bool mustBeNew,
            CancellationToken cancellationToken)
        {
            _finished = true;

            await Task.WhenAll(_travelling).ConfigureAwait(false);

            store.Fail();
            store.Guard(path, ifMatch, mustBeNew);

            using MemoryStream taken = new();

            await rest.CopyToAsync(taken, cancellationToken).ConfigureAwait(false);

            byte[] sentAhead;

            lock (_gate)
            {
                sentAhead = [.. _pieces.SelectMany(piece => piece)];
            }

            byte[] bytes = [.. sentAhead, .. taken.ToArray()];

            store.Assembled.Add(path);

            return store.Store(path, bytes, times);
        }

        public ValueTask DisposeAsync()
        {
            if (!_finished)
            {
                store.CountAbandoned();
            }

            return ValueTask.CompletedTask;
        }

        private async Task TakeAsync(Stream piece, int number)
        {
            try
            {
                if (store.PiecesHeld is Task held)
                {
                    await held.ConfigureAwait(false);
                }

                if (store.RefusePieces)
                {
                    store.CountRefused();

                    throw new ProviderException(ProviderError.InsufficientStorage);
                }

                byte[] bytes = new byte[piece.Length - piece.Position];
                piece.ReadExactly(bytes);

                lock (_gate)
                {
                    _pieces[number] = bytes;
                }

                store.CountSent();
            }
            finally
            {
                await piece.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
