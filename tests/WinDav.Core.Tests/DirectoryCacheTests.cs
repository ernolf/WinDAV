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

    // What the store calls now, for the tests that are about how long ago something changed.
    private static readonly DateTimeOffset s_now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

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
    public async Task ADirectoryThatWasNeverListedIsAnsweredByListingTheOneAroundIt()
    {
        TreeStore store = new();

        store.AddDirectory("/", "v1");
        store.AddDirectory("/music", "v2");

        DirectoryCache cache = Cache(store, Off);

        RemoteEntry self = await cache.GetAsync("/music", TestContext.Current.CancellationToken);

        // The same one request either way, and this one settles every other name in the
        // root along with it.
        Assert.Equal("/music", self.Path);
        Assert.Empty(store.Asked);
        Assert.Equal<string>(["/"], store.Listed);
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
    public async Task ANameTheListingHasAfterItWasFetchedAgainComesOutOfIt()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddFile("/music/one.mp3");

        DirectoryCache cache = Cache(store, Off, s_brief);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await Task.Delay(s_brief + s_brief, TestContext.Current.CancellationToken);

        RemoteEntry entry = await cache.GetAsync("/music/one.mp3", TestContext.Current.CancellationToken);

        // The listing that had run out is fetched again, it has the name, and the name is
        // read out of it. What says a directory holds these entries says what they are.
        Assert.Equal("/music/one.mp3", entry.Path);
        Assert.Empty(store.Asked);
        Assert.Equal<string>(["/music", "/music"], store.Listed);
    }

    [Fact]
    public async Task ANameInADirectoryThatWasNeverListedIsAnsweredByListingIt()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");

        DirectoryCache cache = Cache(store, Off);

        ProviderException failure = await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/.git", TestContext.Current.CancellationToken));

        Assert.Equal(ProviderError.NotFound, failure.Error);
        Assert.Empty(store.Asked);
        Assert.Equal<string>(["/music"], store.Listed);
    }

    [Fact]
    public async Task TheNamesAfterTheFirstInThatDirectoryCostNothing()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddFile("/music/one.mp3");

        DirectoryCache cache = Cache(store, Off);

        RemoteEntry entry = await cache.GetAsync("/music/one.mp3", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/.git", TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/HEAD", TestContext.Current.CancellationToken));

        // What a burst of questions about single names costs: the first is a listing, and
        // every one after it is read out of that listing.
        Assert.Equal("/music/one.mp3", entry.Path);
        Assert.Empty(store.Asked);
        Assert.Equal<string>(["/music"], store.Listed);
    }

    [Fact]
    public async Task TheListingThatSettlesANameReadsNothingAhead()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");
        store.AddFile("/music/one.mp3");

        DirectoryCache cache = Cache(store, new DirectorySettings());

        await cache.GetAsync("/music/one.mp3", TestContext.Current.CancellationToken);

        await Task.Delay(s_brief, TestContext.Current.CancellationToken);

        // Nobody opened that directory; somebody asked about one name in it. What is read
        // ahead belongs to a directory a person is looking at.
        Assert.Equal<string>(["/music"], store.Listed);
    }

    [Fact]
    public async Task ANameFoundInNoOtherDirectoryBuysNoListing()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/photos", "v2");

        DirectoryCache cache = Cache(store, Off);

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/.git", TestContext.Current.CancellationToken));

        // The name is now one that was looked for and found nowhere, so the directory around
        // it is not listed to say so a second time.
        ProviderException failure = await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/photos/.git", TestContext.Current.CancellationToken));

        Assert.Equal(ProviderError.NotFound, failure.Error);
        Assert.Empty(store.Asked);
        Assert.Equal<string>(["/music"], store.Listed);
    }

    [Fact]
    public async Task ADirectoryThatIsHeldAnswersSuchANameOutOfItsListing()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/photos", "v2");
        store.AddFile("/photos/.git");

        DirectoryCache cache = Cache(store, Off);

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/.git", TestContext.Current.CancellationToken));

        await cache.ListAsync("/photos", TestContext.Current.CancellationToken);

        // What is held says what is there, and the rule about names that are nowhere has
        // nothing to say against a listing.
        RemoteEntry entry = await cache.GetAsync("/photos/.git", TestContext.Current.CancellationToken);

        Assert.Equal("/photos/.git", entry.Path);
        Assert.Empty(store.Asked);
        Assert.Equal<string>(["/music", "/photos"], store.Listed);
    }

    [Fact]
    public async Task ANameLookedForNowhereElseStillBuysTheListing()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/photos", "v2");
        store.AddFile("/photos/one.mp3");

        DirectoryCache cache = Cache(store, Off);

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/.git", TestContext.Current.CancellationToken));

        // What a person opens: a name nothing has looked for elsewhere, in a directory
        // nothing holds. It buys the listing, as decision 80 says.
        RemoteEntry entry = await cache.GetAsync("/photos/one.mp3", TestContext.Current.CancellationToken);

        Assert.Equal("/photos/one.mp3", entry.Path);
        Assert.Empty(store.Asked);
        Assert.Equal<string>(["/music", "/photos"], store.Listed);
    }

    [Fact]
    public async Task OneDirectoryIsNotEnoughWhereTwoAreAskedFor()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/photos", "v2");
        store.AddDirectory("/films", "v3");

        DirectoryCache cache = Cache(store, new DirectorySettings { Depth = 0, Probes = 2 });

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/.git", TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/photos/.git", TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/films/.git", TestContext.Current.CancellationToken));

        // The second directory is what burns the name here, so the third is the first to be
        // answered without a request.
        Assert.Empty(store.Asked);
        Assert.Equal<string>(["/music", "/photos"], store.Listed);
    }

    [Fact]
    public async Task ProbesOffListsTheDirectoryForEveryName()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/photos", "v2");

        DirectoryCache cache = Cache(store, new DirectorySettings { Depth = 0, Probes = 0 });

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/.git", TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/photos/.git", TestContext.Current.CancellationToken));

        // No name is ever taken for a probe, which is how a report about a name answered as
        // absent that was there is narrowed down to this rule.
        Assert.Empty(store.Asked);
        Assert.Equal<string>(["/music", "/photos"], store.Listed);
    }

    [Fact]
    public async Task TheRootHasNoDirectoryAroundItAndIsAskedAbout()
    {
        TreeStore store = new();

        store.AddDirectory("/", "v1");

        DirectoryCache cache = Cache(store, Off);

        RemoteEntry root = await cache.GetAsync("/", TestContext.Current.CancellationToken);

        // The one name the rule cannot cover, and the one the mount asks about once.
        Assert.Equal("/", root.Path);
        Assert.Equal<string>(["/"], store.Asked);
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
    public async Task ANameUnderADirectoryTheListingHasIsSettledByListingTheOneAroundIt()
    {
        TreeStore store = new();

        store.AddDirectory("/", "v1");
        store.AddDirectory("/music", "v2");

        DirectoryCache cache = Cache(store, Off);

        await cache.ListAsync("/", TestContext.Current.CancellationToken);

        // '/music' is there, so nothing above says anything about what is under it. What
        // settles the name is a listing of the directory around it, never a question about
        // the name.
        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/live/one.mp3", TestContext.Current.CancellationToken));

        Assert.Empty(store.Asked);
        Assert.Equal<string>(["/", "/music/live"], store.Listed);
    }

    [Fact]
    public async Task ANameUnderAListingOnItsWayWaitsForItInsteadOfAsking()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");

        DirectoryCache cache = Cache(store, Off);

        store.Hold("/music");

        Task<DirectoryListing> listing = cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await WaitFor(store, 1);

        Task<RemoteEntry> question = cache.GetAsync("/music/live/.git", TestContext.Current.CancellationToken);

        store.Release();

        await listing.ConfigureAwait(true);

        ProviderException failure = await Assert.ThrowsAsync<ProviderException>(() => question);

        // A listing is written down after it has come back and been parsed, and these
        // questions arrive in that window. What is on its way counts as held: without that,
        // the directory below is a request of its own for something that is not there.
        Assert.Equal(ProviderError.NotFound, failure.Error);
        Assert.Empty(store.Asked);
        Assert.Equal<string>(["/music"], store.Listed);
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
    public async Task TheDirectoryThatChangedLastIsReadAheadFirst()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1", s_now);
        store.AddDirectory("/music/archive", "v2", s_now.AddYears(-1));
        store.AddDirectory("/music/live", "v3", s_now.AddDays(-30));
        store.AddDirectory("/music/studio", "v4", s_now.AddMinutes(-5));

        // Room for one, so which one it is is the whole of the answer.
        DirectoryCache cache = Cache(store, new DirectorySettings { Requests = 1 });

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await WaitFor(store, 2);
        await Task.Delay(s_brief, TestContext.Current.CancellationToken);

        // By name the round would have spent itself on the one nobody has touched in a year.
        Assert.Equal<string>(["/music", "/music/studio"], store.Listed);
    }

    [Fact]
    public async Task DirectoriesOfTheSameAgeKeepTheOrderTheServerGave()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1", s_now);
        store.AddDirectory("/music/archive", "v2", s_now.AddDays(-7));
        store.AddDirectory("/music/live", "v3", s_now.AddDays(-7));
        store.AddDirectory("/music/studio", "v4", s_now.AddDays(-7));

        DirectoryCache cache = Cache(store, new DirectorySettings { Requests = 1 });

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await WaitFor(store, 2);
        await Task.Delay(s_brief, TestContext.Current.CancellationToken);

        // A tree where nothing has changed behaves as it did before any of this.
        Assert.Equal<string>(["/music", "/music/archive"], store.Listed);
    }

    [Fact]
    public async Task ADirectoryTheStoreGaveNoTimeForGoesLast()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1", s_now);
        store.AddDirectory("/music/archive", "v2");
        store.AddDirectory("/music/studio", "v3", s_now.AddYears(-1));

        DirectoryCache cache = Cache(store, new DirectorySettings { Requests = 1 });

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await WaitFor(store, 2);
        await Task.Delay(s_brief, TestContext.Current.CancellationToken);

        // A year old still beats no answer at all: nothing is known about the one without.
        Assert.Equal<string>(["/music", "/music/studio"], store.Listed);
    }

    [Fact]
    public async Task ADirectoryTheStoreGaveNoTimeForIsStillReadAhead()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1", s_now);
        store.AddDirectory("/music/archive", "v2");
        store.AddDirectory("/music/studio", "v3", s_now.AddMinutes(-5));

        DirectoryCache cache = Cache(store, new DirectorySettings());

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await WaitFor(store, 3);

        // Last is not out. getlastmodified is not guaranteed on a collection, and a store
        // that fills in none of them would otherwise read nothing ahead at all.
        Assert.Equal<string>(["/music", "/music/archive", "/music/studio"], store.Listed.Order());
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
    [Fact]
    public async Task TwoLooksAtOneDirectoryAtOnceAreOneListing()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");

        DirectoryCache cache = Cache(store, Off);

        store.Hold();

        Task<DirectoryListing> first = cache.ListAsync("/music", TestContext.Current.CancellationToken);

        // The second is asked once the first is on its way, which is what the store having
        // written the question down says.
        await WaitFor(() => store.Listed.Count >= 1);

        Task<DirectoryListing> second = cache.ListAsync("/music", TestContext.Current.CancellationToken);

        store.Release();

        DirectoryListing[] both = await Task.WhenAll(first, second);

        // A listing of the root is fifteen kilobytes and about 160 milliseconds, and the
        // second caller used to pay for both again.
        Assert.Equal<string>(["/music"], store.Listed);
        Assert.Same(both[0], both[1]);
    }

    [Fact]
    public async Task AReaderWhoCatchesUpWithWhatIsBeingReadAheadJoinsIt()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");

        DirectoryCache cache = Cache(store, new DirectorySettings());

        // Only the one read ahead, so that the listing somebody waited for goes through and
        // the round behind it is still on the wire when he reaches what it is fetching.
        store.Hold("/music/live");

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await WaitFor(store, 2);

        Task<DirectoryListing> opened = cache.ListAsync("/music/live", TestContext.Current.CancellationToken);

        store.Release();

        await opened.ConfigureAwait(true);

        // Six of the eight requests that overlapped an identical one at a live mount were
        // this: the person had reached the directory the round behind him was fetching, and
        // the listing on its way was not the one he waited for.
        Assert.Equal<string>(["/music", "/music/live"], store.Listed);
    }

    [Fact]
    public async Task AListingThatFailsFailsForEverybodyWaitingOnIt()
    {
        TreeStore store = new();

        DirectoryCache cache = Cache(store, Off);

        store.Hold();

        Task<DirectoryListing> first = cache.ListAsync("/nothing", TestContext.Current.CancellationToken);

        await WaitFor(() => store.Listed.Count >= 1);

        Task<DirectoryListing> second = cache.ListAsync("/nothing", TestContext.Current.CancellationToken);

        store.Release();

        // What the fetch is told is what everybody waiting on it is told. The second had a
        // request of its own before and might have got through where the first did not.
        await Assert.ThrowsAsync<ProviderException>(() => first);
        await Assert.ThrowsAsync<ProviderException>(() => second);

        Assert.Equal<string>(["/nothing"], store.Listed);
    }

    [Fact]
    public async Task TwoQuestionsAboutTheRoomAtOnceAreOneRequest()
    {
        TreeStore store = new();

        DirectoryCache cache = Cache(store, Off);

        store.Hold();

        Task<StorageSpace> first = cache.GetSpaceAsync("/", TestContext.Current.CancellationToken);

        await WaitFor(() => store.SpaceAsked >= 1);

        Task<StorageSpace> second = cache.GetSpaceAsync("/", TestContext.Current.CancellationToken);

        store.Release();

        await Task.WhenAll(first, second);

        Assert.Equal(1, store.SpaceAsked);

        // Nothing is kept of it beyond the fetch: the next question is a request of its own,
        // which is what the volume's own interval asks for.
        await cache.GetSpaceAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(2, store.SpaceAsked);
    }

    private static DirectorySettings Off => new() { Depth = 0 };

    private static RequestGate Gate() => new(2, NullLogger.Instance);

    private static DirectoryCache Cache(TreeStore store, DirectorySettings settings, TimeSpan? lifetime = null) =>
        new(store, lifetime ?? s_ample, settings, Gate());

    // Listing ahead happens behind whoever asked, so a test that is about it has to wait for
    // it. It is done in microseconds against a dictionary; the patience is for a machine
    // under load, and reaching the end of it is the failure the assertion afterwards reports.
    private static Task WaitFor(TreeStore store, int listings) =>
        WaitFor(() => store.Listed.Count >= listings);

    private static async Task WaitFor(Func<bool> until)
    {
        long deadline = Environment.TickCount64 + (long)s_patience.TotalMilliseconds;

        while (!until() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
    }

    // A store of directories and files, each directory with a version of its own, which is
    // what a server gives one and what everything here turns on.
    private sealed class TreeStore : IStorageProvider
    {
        private readonly Dictionary<string, string?> _directories = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTimeOffset?> _times = new(StringComparer.Ordinal);
        private readonly HashSet<string> _files = new(StringComparer.Ordinal);
        private readonly Lock _sync = new();

        private string? _refused;
        private string? _holding;
        private TaskCompletionSource? _held;

        public List<string> Listed { get; } = [];

        public List<string> Asked { get; } = [];

        public int SpaceAsked { get; private set; }

        public void AddDirectory(string path, string? version, DateTimeOffset? modified = null)
        {
            _directories[path] = version;
            _times[path] = modified;
        }

        public void AddFile(string path) => _files.Add(path);

        public void SetVersion(string path, string? version) => _directories[path] = version;

        // Everything under a path answers that the server will not take another request.
        public void RefuseBelow(string path) => _refused = path.EndsWith('/') ? path : path + '/';

        // Holds answers back until they are let go, which is what puts a second question on
        // its way while the first is still in flight. A path holds that one alone, so that
        // what a listing sets off behind it can be caught while it is still on the wire.
        public void Hold(string? path = null)
        {
            _holding = path;
            _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Release()
        {
            TaskCompletionSource? held = _held;

            _held = null;

            held?.SetResult();
        }

        public async Task<DirectoryListing> ListAsync(string path, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Listed.Add(path);
            }

            await Held(path).ConfigureAwait(false);

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
                    children.Add(new RemoteEntry(directory.Key, isDirectory: true)
                    {
                        ETag = directory.Value,
                        LastModified = _times.GetValueOrDefault(directory.Key),
                    });
                }
            }

            foreach (string file in _files)
            {
                if (IsIn(file, path))
                {
                    children.Add(new RemoteEntry(file, isDirectory: false));
                }
            }

            // A server hands a directory over in an order of its own, and by name is the one
            // the read-ahead used to queue in. What a dictionary enumerates in is no order to
            // rest a test on.
            children.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));

            return new DirectoryListing(children, new RemoteEntry(path, isDirectory: true) { ETag = version });
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

        public async Task<StorageSpace> GetSpaceAsync(string path, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                SpaceAsked++;
            }

            await Held(path).ConfigureAwait(false);

            return StorageSpace.Unknown;
        }

        private Task Held(string path) =>
            _held is { } held && (_holding is null || string.Equals(_holding, path, StringComparison.Ordinal))
                ? held.Task
                : Task.CompletedTask;

        private static bool IsIn(string path, string directory)
        {
            string below = directory.EndsWith('/') ? directory : directory + '/';

            return path.StartsWith(below, StringComparison.Ordinal)
                && !path.AsSpan(below.Length).Contains('/');
        }
    }
}
