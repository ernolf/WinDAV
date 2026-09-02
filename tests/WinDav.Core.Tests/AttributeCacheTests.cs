// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Abstractions;
using WinDav.Core.Providers;
using Xunit;

namespace WinDav.Core.Tests;

// What the metadata costs, counted in requests. The store underneath writes down every
// question it was asked, so what is asserted here is how often the server was troubled by a
// sequence a person would produce — and that what came back is what the server said.
public sealed class AttributeCacheTests
{
    // Short enough to run out inside a test, long enough that a machine under load does not
    // let it run out halfway through one that is about the keeping.
    private static readonly TimeSpan s_brief = TimeSpan.FromMilliseconds(200);

    // How long a test waits for a question asked from somewhere other than the test. Never
    // reached when it arrives, and it arrives in microseconds against a store that is a set.
    private static readonly TimeSpan s_patience = TimeSpan.FromSeconds(10);

    private static async Task WaitFor(Func<bool> until)
    {
        long deadline = Environment.TickCount64 + (long)s_patience.TotalMilliseconds;

        while (!until() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task TheSecondQuestionAboutAPathIsAnsweredFromTheFirst()
    {
        CountingStore store = new();

        store.Add("/notes.txt");

        AttributeCache cache = new(store);

        // What opening a file does: WinFsp asks whether the name may be opened, and then
        // opens it, milliseconds apart.
        RemoteEntry asked = await cache.GetAsync("/notes.txt", TestContext.Current.CancellationToken);
        RemoteEntry opened = await cache.GetAsync("/notes.txt", TestContext.Current.CancellationToken);

        Assert.Same(asked, opened);
        Assert.Equal<string>(["/notes.txt"], store.Asked);
    }

    [Fact]
    public async Task AListingAnswersTheOpensThatFollowIt()
    {
        CountingStore store = new();

        store.Add("/music");
        store.Add("/music/one.mp3");
        store.Add("/music/two.mp3");
        store.Add("/music/three.mp3");

        AttributeCache cache = new(store);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        string[] files = ["/music/one.mp3", "/music/two.mp3", "/music/three.mp3"];

        foreach (string path in files)
        {
            RemoteEntry entry = await cache.GetAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(path, entry.Path);
        }

        // A listing and three opens is one request, because the listing was told about every
        // sibling at the price of the one entry it was asked about.
        Assert.Equal<string>(["/music"], store.Listed);
        Assert.Empty(store.Asked);
    }

    [Fact]
    public async Task WhatHasRunOutIsAskedAgain()
    {
        CountingStore store = new();

        store.Add("/notes.txt");

        AttributeCache cache = new(store, s_brief);

        await cache.GetAsync("/notes.txt", TestContext.Current.CancellationToken);
        await Task.Delay(s_brief + s_brief, TestContext.Current.CancellationToken);
        await cache.GetAsync("/notes.txt", TestContext.Current.CancellationToken);

        // Somebody else writing on the server is the point of the server, and nothing here is
        // told when they have.
        Assert.Equal<string>(["/notes.txt", "/notes.txt"], store.Asked);
    }

    [Fact]
    public async Task SwitchedOffThereIsNoLayerAtAll()
    {
        CountingStore store = new();

        store.Add("/notes.txt");

        IStorageProvider off = AttributeCache.Over(store, TimeSpan.Zero);

        Assert.Same(store, off);

        await off.GetAsync("/notes.txt", TestContext.Current.CancellationToken);
        await off.GetAsync("/notes.txt", TestContext.Current.CancellationToken);

        // A request per question, which is what the mount did before this layer existed and
        // what a report about a stale directory is narrowed down with.
        Assert.Equal<string>(["/notes.txt", "/notes.txt"], store.Asked);
    }

    [Fact]
    public async Task NothingIsKeptAboutAPathThatIsNotThere()
    {
        CountingStore store = new();

        AttributeCache cache = new(store);

        // The Explorer asks about names that are not there several times per window, and the
        // one thing worse than asking again is answering that a file is missing after
        // somebody has put it there.
        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/desktop.ini", TestContext.Current.CancellationToken));

        store.Add("/desktop.ini");

        RemoteEntry found = await cache.GetAsync("/desktop.ini", TestContext.Current.CancellationToken);

        Assert.Equal("/desktop.ini", found.Path);
        Assert.Equal<string>(["/desktop.ini", "/desktop.ini"], store.Asked);
    }

    [Fact]
    public async Task AWrittenFileIsAskedAboutAgain()
    {
        CountingStore store = new();

        store.Add("/notes.txt");

        AttributeCache cache = new(store);

        await cache.GetAsync("/notes.txt", TestContext.Current.CancellationToken);

        using MemoryStream content = new([1, 2, 3]);

        await cache.WriteAsync("/notes.txt", content, cancellationToken: TestContext.Current.CancellationToken);
        await cache.GetAsync("/notes.txt", TestContext.Current.CancellationToken);

        // The length that was held is the length before the write, and this mount is the one
        // thing here that knows the write happened.
        Assert.Equal<string>(["/notes.txt", "/notes.txt"], store.Asked);
    }

    [Fact]
    public async Task ADeletedDirectoryTakesWhatWasInItWithIt()
    {
        CountingStore store = new();

        store.Add("/music");
        store.Add("/music/one.mp3");

        AttributeCache cache = new(store);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);
        await cache.DeleteAsync("/music", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/one.mp3", TestContext.Current.CancellationToken));

        Assert.Equal<string>(["/music/one.mp3"], store.Asked);
    }

    [Fact]
    public async Task AMoveForgetsBothEnds()
    {
        CountingStore store = new();

        store.Add("/one.mp3");
        store.Add("/two.mp3");

        AttributeCache cache = new(store);

        await cache.GetAsync("/one.mp3", TestContext.Current.CancellationToken);
        await cache.GetAsync("/two.mp3", TestContext.Current.CancellationToken);

        await cache.MoveAsync("/one.mp3", "/two.mp3", overwrite: true, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/one.mp3", TestContext.Current.CancellationToken));

        await cache.GetAsync("/two.mp3", TestContext.Current.CancellationToken);

        Assert.Equal<string>(
            ["/one.mp3", "/two.mp3", "/one.mp3", "/two.mp3"],
            store.Asked);
    }

    [Fact]
    public async Task ACopyLeavesTheSourceAsItWas()
    {
        CountingStore store = new();

        store.Add("/one.mp3");
        store.Add("/two.mp3");

        AttributeCache cache = new(store);

        await cache.GetAsync("/one.mp3", TestContext.Current.CancellationToken);
        await cache.GetAsync("/two.mp3", TestContext.Current.CancellationToken);

        await cache.CopyAsync("/one.mp3", "/two.mp3", overwrite: true, TestContext.Current.CancellationToken);

        await cache.GetAsync("/one.mp3", TestContext.Current.CancellationToken);
        await cache.GetAsync("/two.mp3", TestContext.Current.CancellationToken);

        // Only what was written to is asked about again.
        Assert.Equal<string>(["/one.mp3", "/two.mp3", "/two.mp3"], store.Asked);
    }

    [Fact]
    public async Task TwoPathsThatDifferOnlyInTheirCaseAreTwoEntries()
    {
        CountingStore store = new();

        store.Add("/Notes.txt");
        store.Add("/notes.txt");

        AttributeCache cache = new(store);

        RemoteEntry upper = await cache.GetAsync("/Notes.txt", TestContext.Current.CancellationToken);
        RemoteEntry lower = await cache.GetAsync("/notes.txt", TestContext.Current.CancellationToken);

        // A store that keeps case has two files here, and answering the one from the other
        // would hand back a different file than was asked for.
        Assert.Equal("/Notes.txt", upper.Path);
        Assert.Equal("/notes.txt", lower.Path);
        Assert.Equal<string>(["/Notes.txt", "/notes.txt"], store.Asked);
    }

    [Fact]
    public async Task NeitherTheBytesNorTheRoomLeftAreEverAnsweredFromMemory()
    {
        CountingStore store = new();

        store.Add("/notes.txt");

        AttributeCache cache = new(store);

        await cache.GetSpaceAsync("/", TestContext.Current.CancellationToken);
        await cache.GetSpaceAsync("/", TestContext.Current.CancellationToken);

        using Stream content = await cache.OpenReadAsync(
            "/notes.txt", cancellationToken: TestContext.Current.CancellationToken);

        // Nothing here is a question about attributes: the room left is a figure the volume
        // holds for itself, and bytes are the read path's business.
        Assert.Equal(2, store.SpaceAsked);
        Assert.Equal<string>(["/notes.txt"], store.Opened);
    }

    [Fact]
    public async Task TheSecondQuestionAboutAPathInFlightWaitsForTheFirst()
    {
        CountingStore store = new();

        store.Add("/music/one.mp3");

        AttributeCache cache = new(store);

        store.Hold();

        // What opening a file does when the first answer is not back yet: WinFsp asks whether
        // the name may be opened, and opens it, and both used to be requests.
        Task<RemoteEntry> asked = cache.GetAsync("/music/one.mp3", TestContext.Current.CancellationToken);

        await WaitFor(() => store.Asked.Count >= 1);

        Task<RemoteEntry> opened = cache.GetAsync("/music/one.mp3", TestContext.Current.CancellationToken);

        store.Release();

        RemoteEntry[] both = await Task.WhenAll(asked, opened);

        Assert.Equal<string>(["/music/one.mp3"], store.Asked);
        Assert.Same(both[0], both[1]);
    }

    [Fact]
    public async Task AQuestionThatFailsFailsForEverybodyWaitingOnIt()
    {
        CountingStore store = new();

        AttributeCache cache = new(store);

        store.Hold();

        Task<RemoteEntry> first = cache.GetAsync("/nothing", TestContext.Current.CancellationToken);

        await WaitFor(() => store.Asked.Count >= 1);

        Task<RemoteEntry> second = cache.GetAsync("/nothing", TestContext.Current.CancellationToken);

        store.Release();

        // What the fetch is told is what everybody waiting on it is told, and nothing of it
        // is kept: what is not there is the one answer that must not be remembered.
        await Assert.ThrowsAsync<ProviderException>(() => first);
        await Assert.ThrowsAsync<ProviderException>(() => second);

        Assert.Equal<string>(["/nothing"], store.Asked);
    }

    [Fact]
    public async Task TwoQuestionsAboutTheRoomAtOnceAreOneRequest()
    {
        CountingStore store = new();

        AttributeCache cache = new(store);

        store.Hold();

        Task<StorageSpace> first = cache.GetSpaceAsync("/", TestContext.Current.CancellationToken);

        await WaitFor(() => store.SpaceAsked >= 1);

        Task<StorageSpace> second = cache.GetSpaceAsync("/", TestContext.Current.CancellationToken);

        store.Release();

        await Task.WhenAll(first, second);

        Assert.Equal(1, store.SpaceAsked);
    }

    [Fact]
    public void ACacheThatHoldsNothingIsALayerThatShouldNotHaveBeenBuilt()
    {
        CountingStore store = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => new AttributeCache(store, TimeSpan.Zero));
        Assert.Throws<ArgumentNullException>(() => new AttributeCache(null!));
        Assert.Throws<ArgumentNullException>(() => AttributeCache.Over(null!, TimeSpan.FromSeconds(1)));
    }

    // A store that answers from a set of paths and writes down every question. Only what the
    // cache reaches is answered; what it hands through untouched is counted and no more.
    private sealed class CountingStore : IStorageProvider
    {
        private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
        private readonly Lock _sync = new();

        private TaskCompletionSource? _held;

        public List<string> Asked { get; } = [];

        public List<string> Listed { get; } = [];

        public List<string> Opened { get; } = [];

        public int SpaceAsked { get; private set; }

        public void Add(string path) => _paths.Add(path);

        // Holds every answer back until it is let go, which is what puts a second question on
        // its way while the first is still in flight.
        public void Hold() => _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release()
        {
            TaskCompletionSource? held = _held;

            _held = null;

            held?.SetResult();
        }

        public Task<DirectoryListing> ListAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            Listed.Add(path);

            string below = path.EndsWith('/') ? path : path + '/';

            List<RemoteEntry> children = [];

            foreach (string held in _paths)
            {
                if (held.StartsWith(below, StringComparison.Ordinal)
                    && !held.AsSpan(below.Length).Contains('/'))
                {
                    children.Add(new RemoteEntry(held, isDirectory: false));
                }
            }

            // Nothing about the directory itself: a store is entitled to describe only what
            // is in it, and the layer above has to hold up either way.
            return Task.FromResult(new DirectoryListing(children));
        }

        public async Task<RemoteEntry> GetAsync(string path, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Asked.Add(path);
            }

            await Held().ConfigureAwait(false);

            return _paths.Contains(path)
                ? new RemoteEntry(path, isDirectory: false)
                : throw new ProviderException(ProviderError.NotFound, $"Nothing at {path}.");
        }

        public Task<Stream> OpenReadAsync(
            string path,
            long offset = 0,
            long? count = null,
            CancellationToken cancellationToken = default)
        {
            Opened.Add(path);

            return Task.FromResult<Stream>(new MemoryStream());
        }

        public Task<string?> WriteAsync(
            string path,
            Stream content,
            string? ifMatch = null,
            CancellationToken cancellationToken = default)
        {
            _paths.Add(path);

            return Task.FromResult<string?>(null);
        }

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            _paths.Add(path);

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
        {
            string below = path.EndsWith('/') ? path : path + '/';

            _paths.RemoveWhere(held =>
                string.Equals(held, path, StringComparison.Ordinal)
                || held.StartsWith(below, StringComparison.Ordinal));

            return Task.CompletedTask;
        }

        public Task MoveAsync(
            string sourcePath,
            string destinationPath,
            bool overwrite = false,
            CancellationToken cancellationToken = default)
        {
            _paths.Remove(sourcePath);
            _paths.Add(destinationPath);

            return Task.CompletedTask;
        }

        public Task CopyAsync(
            string sourcePath,
            string destinationPath,
            bool overwrite = false,
            CancellationToken cancellationToken = default)
        {
            _paths.Add(destinationPath);

            return Task.CompletedTask;
        }

        public async Task<StorageSpace> GetSpaceAsync(string path, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                SpaceAsked++;
            }

            await Held().ConfigureAwait(false);

            return StorageSpace.Unknown;
        }

        private Task Held() => _held?.Task ?? Task.CompletedTask;
    }
}
