// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using WinDav.Abstractions;

namespace WinDav.Fs.Tests;

// A store held in memory, with the same seam a real provider has: paths with slashes, and
// a ProviderException for everything that cannot be done. Only what this cut of the file
// system reaches is implemented; the write half throws, so a test that reached it by
// accident fails loudly instead of passing quietly.
internal sealed class FakeStore : IStorageProvider
{
    private readonly Dictionary<string, RemoteEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _content = new(StringComparer.OrdinalIgnoreCase);

    public FakeStore()
    {
        AddDirectory("/");
    }

    // What the file system asked to read, in the store's own spelling.
    public List<string> Opened { get; } = [];

    // Every range that was asked for, in order. One entry is one request, which is what a
    // test about the read path counts.
    public List<(long Offset, long? Count)> Reads { get; } = [];

    public long LastOffset { get; private set; }

    public long? LastCount { get; private set; }

    // Set to make every call fail from then on, which is how a server going away mid-read
    // is arranged.
    public ProviderError? FailWith { get; set; }

    // What the store says about its room. Nothing by default, which is a store that keeps no
    // such figure.
    public StorageSpace Space { get; set; } = StorageSpace.Unknown;

    public void AddDirectory(string path) => _entries[path] = new RemoteEntry(path, true);

    public void AddFile(
        string path,
        string content,
        EntryPermissions? permissions = null,
        DateTimeOffset? lastModified = null)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);

        _entries[path] = new RemoteEntry(path, false)
        {
            Length = bytes.Length,
            Permissions = permissions,
            LastModified = lastModified,
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

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken cancellationToken)
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

        return Task.FromResult<IReadOnlyList<RemoteEntry>>(children);
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

    public Task<string?> WriteAsync(
        string path,
        Stream content,
        string? ifMatch,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task DeleteAsync(string path, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task MoveAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task CopyAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    private static string ParentOf(string path)
    {
        int slash = path.LastIndexOf('/');

        return slash <= 0 ? "/" : path[..slash];
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
}
