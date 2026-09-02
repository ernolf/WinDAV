// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using WinDav.Abstractions;
using WinDav.Core.Providers;
using Xunit;

namespace WinDav.Core.Tests;

// What browsing costs, counted in listings. The store underneath writes down every directory
// it was asked to list, so what is asserted here is how often the server was troubled by a
// sequence a person would produce, and that what came back is what the server said.
public sealed class DirectoryCacheTests
{
    // Short enough to run out inside a test, long enough that a machine under load does not
    // let it run out halfway through one that is about the holding.
    private static readonly TimeSpan s_brief = TimeSpan.FromMilliseconds(200);

    // Long enough that nothing runs out while a test is about something else.
    private static readonly TimeSpan s_ample = TimeSpan.FromMinutes(5);

    // How long a test waits for what happens behind whoever asked. Never reached when the
    // work is done, and it is done in microseconds against a store that is a dictionary.
    private static readonly TimeSpan s_patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task TheSecondLookAtADirectoryIsAnsweredFromTheFirst()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");

        DirectoryCache cache = Cache(store, Off);

        DirectoryListing first = await cache.ListAsync("/music", TestContext.Current.CancellationToken);
        DirectoryListing again = await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        Assert.Same(first.Entries, again.Entries);
        Assert.Equal<string>(["/music"], store.Listed);
    }

    [Fact]
    public async Task AListingIsAskedForAgainOnceItHasRunOut()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");

        DirectoryCache cache = Cache(store, Off, s_brief);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await Task.Delay(s_brief + s_brief, TestContext.Current.CancellationToken);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        Assert.Equal<string>(["/music", "/music"], store.Listed);
    }

    [Fact]
    public async Task WhatADirectoryIsIsAnsweredByListingIt()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");

        DirectoryCache cache = Cache(store, Off);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        RemoteEntry self = await cache.GetAsync("/music", TestContext.Current.CancellationToken);

        // The beat the whole idea rides on: a window standing open asks what the directory
        // is every few seconds, and that question is answered with the contents rather than
        // without them. Nothing was asked about the directory on its own.
        Assert.Equal("/music", self.Path);
        Assert.Empty(store.Asked);
    }

    [Fact]
    public async Task ADirectoryThatWasNeverListedIsAskedAboutOnItsOwn()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");

        DirectoryCache cache = Cache(store, Off);

        await cache.GetAsync("/music", TestContext.Current.CancellationToken);

        Assert.Equal<string>(["/music"], store.Asked);
        Assert.Empty(store.Listed);
    }

    [Fact]
    public async Task ANameThatIsNotInAHeldListingIsAnsweredWithoutAsking()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddFile("/music/one.mp3");

        DirectoryCache cache = Cache(store, Off);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        // What a status cache asks about every folder a window shows, over and over.
        ProviderException failure = await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/.git", TestContext.Current.CancellationToken));

        Assert.Equal(ProviderError.NotFound, failure.Error);
        Assert.Empty(store.Asked);
    }

    [Fact]
    public async Task ANameInTheWrongCaseIsNotInTheListingEither()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddFile("/music/one.mp3");

        DirectoryCache cache = Cache(store, Off);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        // The store keeps case, so this is the right answer and not a shortcut around one.
        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/ONE.MP3", TestContext.Current.CancellationToken));

        Assert.Empty(store.Asked);
    }

    [Fact]
    public async Task AListingThatHasRunOutIsFetchedAgainForTheNameAskedFor()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");

        DirectoryCache cache = Cache(store, Off, s_brief);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await Task.Delay(s_brief + s_brief, TestContext.Current.CancellationToken);

        // These names arrive in bursts far shorter than a listing lives, so the one listing
        // answers a whole burst that would otherwise be one request per name.
        ProviderException failure = await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/.git", TestContext.Current.CancellationToken));

        Assert.Equal(ProviderError.NotFound, failure.Error);
        Assert.Empty(store.Asked);
        Assert.Equal<string>(["/music", "/music"], store.Listed);
    }

    [Fact]
    public async Task ANameTheListingHasAfterItWasFetchedAgainIsAskedAbout()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddFile("/music/one.mp3");

        DirectoryCache cache = Cache(store, Off, s_brief);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await Task.Delay(s_brief + s_brief, TestContext.Current.CancellationToken);

        await cache.GetAsync("/music/one.mp3", TestContext.Current.CancellationToken);

        // The listing that had run out is fetched again, it has the name, and what the name
        // is remains the server's to say.
        Assert.Equal<string>(["/music/one.mp3"], store.Asked);
        Assert.Equal<string>(["/music", "/music"], store.Listed);
    }

    [Fact]
    public async Task ANameInADirectoryThatWasNeverListedIsAskedAbout()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");

        DirectoryCache cache = Cache(store, Off);

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/.git", TestContext.Current.CancellationToken));

        Assert.Equal<string>(["/music/.git"], store.Asked);
        Assert.Empty(store.Listed);
    }

    [Fact]
    public async Task ANameUnderADirectoryTheListingDoesNotHaveIsAnsweredWithoutAsking()
    {
        TreeStore store = new();

        store.AddDirectory("/", "v1");
        store.AddDirectory("/music", "v2");

        DirectoryCache cache = Cache(store, Off);

        await cache.ListAsync("/", TestContext.Current.CancellationToken);

        // The whole path arrives at once, and nothing has ever listed '/etc' because there
        // is no '/etc'. What says so is the listing two levels up.
        ProviderException failure = await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/etc/gnutls/config", TestContext.Current.CancellationToken));

        Assert.Equal(ProviderError.NotFound, failure.Error);
        Assert.Empty(store.Asked);
    }

    [Fact]
    public async Task ANameUnderADirectoryTheListingHasIsAskedAbout()
    {
        TreeStore store = new();

        store.AddDirectory("/", "v1");
        store.AddDirectory("/music", "v2");

        DirectoryCache cache = Cache(store, Off);

        await cache.ListAsync("/", TestContext.Current.CancellationToken);

        // '/music' is there, so what is in it is the server's to say and nothing above has
        // been told about it.
        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/live/one.mp3", TestContext.Current.CancellationToken));

        Assert.Equal<string>(["/music/live/one.mp3"], store.Asked);
    }

    [Fact]
    public async Task AVersionThatHasNotChangedKeepsWhatIsHeldBelowIt()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");
        store.AddFile("/music/live/one.mp3");

        DirectoryCache cache = Cache(store, Off, s_brief);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);
        await cache.ListAsync("/music/live", TestContext.Current.CancellationToken);

        await Task.Delay(s_brief + s_brief, TestContext.Current.CancellationToken);

        // The one request a window standing open makes anyway. It carries the version of
        // every child directory, and none of them has moved.
        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        DirectoryListing live = await cache.ListAsync("/music/live", TestContext.Current.CancellationToken);

        Assert.Equal<string>(["/music/live/one.mp3"], live.Entries.Select(entry => entry.Path));
        Assert.Equal<string>(["/music", "/music/live", "/music"], store.Listed);
    }

    [Fact]
    public async Task AVersionThatHasChangedThrowsAwayWhatIsHeldBelowIt()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");
        store.AddFile("/music/live/one.mp3");

        DirectoryCache cache = Cache(store, Off, s_brief);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);
        await cache.ListAsync("/music/live", TestContext.Current.CancellationToken);

        // What somebody else did. A server propagates it into every directory above, which
        // is what makes one listing enough to find out.
        store.AddFile("/music/live/two.mp3");
        store.SetVersion("/music/live", "v3");

        await Task.Delay(s_brief + s_brief, TestContext.Current.CancellationToken);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        DirectoryListing live = await cache.ListAsync("/music/live", TestContext.Current.CancellationToken);

        Assert.Equal<string>(
            ["/music/live/one.mp3", "/music/live/two.mp3"],
            live.Entries.Select(entry => entry.Path).Order());

        Assert.Equal<string>(["/music", "/music/live", "/music", "/music/live"], store.Listed);
    }

    [Fact]
    public async Task AStoreWithNoVersionsIsNeverVouchedFor()
    {
        TreeStore store = new();

        store.AddDirectory("/music", version: null);
        store.AddDirectory("/music/live", version: null);

        DirectoryCache cache = Cache(store, Off, s_brief);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);
        await cache.ListAsync("/music/live", TestContext.Current.CancellationToken);

        await Task.Delay(s_brief + s_brief, TestContext.Current.CancellationToken);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);
        await cache.ListAsync("/music/live", TestContext.Current.CancellationToken);

        // Nothing was proven and nothing was thrown away: what is held ages out by itself,
        // which is what a store without versions for directories did before any of this.
        Assert.Equal<string>(["/music", "/music/live", "/music", "/music/live"], store.Listed);
    }

    [Fact]
    public async Task ListingADirectoryListsTheDirectoriesInIt()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");
        store.AddDirectory("/music/studio", "v3");
        store.AddFile("/music/cover.jpg");

        DirectoryCache cache = Cache(store, new DirectorySettings());

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await WaitFor(store, 3);

        // And the level below them is not touched: one level is what was asked for.
        Assert.Equal<string>(["/music", "/music/live", "/music/studio"], store.Listed.Order());

        // What the person opens next is there already, and costs nothing.
        await cache.ListAsync("/music/live", TestContext.Current.CancellationToken);

        Assert.Equal(3, store.Listed.Count);
    }

    [Fact]
    public async Task ARoundGoesNoFurtherThanItsCeiling()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");

        for (int number = 0; number < 10; number++)
        {
            store.AddDirectory($"/music/{number}", $"v{number}");
        }

        DirectoryCache cache = Cache(store, new DirectorySettings { Requests = 4 });

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await WaitFor(store, 5);

        // The one somebody waited for, and four behind it. What is left of the round is
        // dropped rather than carried over.
        await Task.Delay(s_brief, TestContext.Current.CancellationToken);

        Assert.Equal(5, store.Listed.Count);
    }

    [Fact]
    public async Task ADepthOfNothingListsNothingAhead()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");

        DirectoryCache cache = Cache(store, new DirectorySettings { Depth = 0 });

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await Task.Delay(s_brief, TestContext.Current.CancellationToken);

        Assert.Equal<string>(["/music"], store.Listed);
    }

    [Fact]
    public async Task AServerThatSaysItIsBusyEndsTheRound()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");

        for (int number = 0; number < 10; number++)
        {
            store.AddDirectory($"/music/{number}", $"v{number}");
        }

        // Everything below the directory somebody asked for.
        store.RefuseBelow("/music");

        DirectoryCache cache = Cache(store, new DirectorySettings());

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await WaitFor(store, 2);
        await Task.Delay(s_brief, TestContext.Current.CancellationToken);

        // The one that was asked for, and one refusal. Answering a refusal by asking nine
        // more times is how a shared server ends up with a rule against the program.
        Assert.Equal(2, store.Listed.Count);
    }

    [Fact]
    public async Task WritingThrowsAwayTheListingOfTheDirectoryWrittenIn()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");

        DirectoryCache cache = Cache(store, Off);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        using (MemoryStream content = new([1, 2, 3]))
        {
            await cache.WriteAsync(
                "/music/new.mp3",
                content,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        Assert.Equal<string>(["/music", "/music"], store.Listed);
    }

    [Fact]
    public async Task DeletingADirectoryThrowsAwayWhatWasHeldInsideIt()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");
        store.AddFile("/music/live/one.mp3");

        DirectoryCache cache = Cache(store, Off);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);
        await cache.ListAsync("/music/live", TestContext.Current.CancellationToken);

        await cache.DeleteAsync("/music/live", TestContext.Current.CancellationToken);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        Assert.Equal<string>(["/music", "/music/live", "/music"], store.Listed);

        // And what was inside it is gone with it rather than answered from memory.
        await Assert.ThrowsAsync<ProviderException>(
            () => cache.ListAsync("/music/live", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NoMoreListingsAreHeldThanTheCeilingAllows()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/one", "v2");
        store.AddDirectory("/music/two", "v3");

        DirectoryCache cache = Cache(store, new DirectorySettings { Depth = 0, Directories = 2 });

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);
        await cache.ListAsync("/music/one", TestContext.Current.CancellationToken);
        await cache.ListAsync("/music/two", TestContext.Current.CancellationToken);

        // Three were listed and two may be held, so the first one is gone.
        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        Assert.Equal<string>(["/music", "/music/one", "/music/two", "/music"], store.Listed);
    }

    [Fact]
    public void HoldingNothingLeavesTheStoreAsItIs()
    {
        TreeStore store = new();

        Assert.Same(
            store,
            DirectoryCache.Over(
                store,
                s_ample,
                new DirectorySettings { Directories = 0 },
                Gate(),
                TestContext.Current.CancellationToken));

        Assert.Same(store, DirectoryCache.Over(
                store,
                TimeSpan.Zero,
                new DirectorySettings(),
                Gate(),
                TestContext.Current.CancellationToken));
    }

    // A cache that holds but never lists ahead, which is what a test about the holding wants.
    private static DirectorySettings Off => new() { Depth = 0 };

    private static RequestGate Gate() => new(2, NullLogger.Instance);

    private static DirectoryCache Cache(TreeStore store, DirectorySettings settings, TimeSpan? lifetime = null) =>
        new(store, lifetime ?? s_ample, settings, Gate());

    // Listing ahead happens behind whoever asked, so a test that is about it has to wait for
    // it. It is done in microseconds against a dictionary; the patience is for a machine
    // under load, and reaching the end of it is the failure the assertion afterwards reports.
    private static async Task WaitFor(TreeStore store, int listings)
    {
        long deadline = Environment.TickCount64 + (long)s_patience.TotalMilliseconds;

        while (store.Listed.Count < listings && Environment.TickCount64 < deadline)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
    }

    // A store of directories and files, each directory with a version of its own, which is
    // what a server gives one and what everything here turns on.
    private sealed class TreeStore : IStorageProvider
    {
        private readonly Dictionary<string, string?> _directories = new(StringComparer.Ordinal);
        private readonly HashSet<string> _files = new(StringComparer.Ordinal);
        private readonly Lock _sync = new();

        private string? _refused;

        public List<string> Listed { get; } = [];

        public List<string> Asked { get; } = [];

        public void AddDirectory(string path, string? version) => _directories[path] = version;

        public void AddFile(string path) => _files.Add(path);

        public void SetVersion(string path, string? version) => _directories[path] = version;

        // Everything under a path answers that the server will not take another request.
        public void RefuseBelow(string path) => _refused = path.EndsWith('/') ? path : path + '/';

        public Task<DirectoryListing> ListAsync(string path, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Listed.Add(path);
            }

            if (_refused is { } below && path.StartsWith(below, StringComparison.Ordinal))
            {
                throw new ProviderException(ProviderError.Busy, "Try again later.");
            }

            if (!_directories.TryGetValue(path, out string? version))
            {
                throw new ProviderException(ProviderError.NotFound, $"Nothing at {path}.");
            }

            List<RemoteEntry> children = [];

            foreach (KeyValuePair<string, string?> directory in _directories)
            {
                if (IsIn(directory.Key, path))
                {
                    children.Add(new RemoteEntry(directory.Key, isDirectory: true) { ETag = directory.Value });
                }
            }

            foreach (string file in _files)
            {
                if (IsIn(file, path))
                {
                    children.Add(new RemoteEntry(file, isDirectory: false));
                }
            }

            return Task.FromResult(
                new DirectoryListing(children, new RemoteEntry(path, isDirectory: true) { ETag = version }));
        }

        public Task<RemoteEntry> GetAsync(string path, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Asked.Add(path);
            }

            if (_directories.TryGetValue(path, out string? version))
            {
                return Task.FromResult(new RemoteEntry(path, isDirectory: true) { ETag = version });
            }

            return _files.Contains(path)
                ? Task.FromResult(new RemoteEntry(path, isDirectory: false))
                : throw new ProviderException(ProviderError.NotFound, $"Nothing at {path}.");
        }

        public Task<Stream> OpenReadAsync(
            string path,
            long offset = 0,
            long? count = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task<string?> WriteAsync(
            string path,
            Stream content,
            string? ifMatch = null,
            CancellationToken cancellationToken = default)
        {
            _files.Add(path);

            return Task.FromResult<string?>(null);
        }

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            _directories[path] = null;

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
        {
            string below = path + '/';

            _directories.Remove(path);
            _files.Remove(path);

            foreach (string held in _directories.Keys.Where(key => key.StartsWith(below, StringComparison.Ordinal)).ToList())
            {
                _directories.Remove(held);
            }

            _files.RemoveWhere(held => held.StartsWith(below, StringComparison.Ordinal));

            return Task.CompletedTask;
        }

        public Task MoveAsync(
            string sourcePath,
            string destinationPath,
            bool overwrite = false,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CopyAsync(
            string sourcePath,
            string destinationPath,
            bool overwrite = false,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<StorageSpace> GetSpaceAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(StorageSpace.Unknown);

        private static bool IsIn(string path, string directory)
        {
            string below = directory.EndsWith('/') ? directory : directory + '/';

            return path.StartsWith(below, StringComparison.Ordinal)
                && !path.AsSpan(below.Length).Contains('/');
        }
    }
}
