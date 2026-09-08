// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;
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

    // A lifetime with room on both sides of its half, for the tests about the renewal. Long
    // where a lifetime does not have to be, because these are the tests that need a wait to
    // land between two marks rather than past one: a machine running the whole suite hands a
    // 200ms delay back a second late often enough, and a second of that is what the room
    // above s_halfway is for.
    private static readonly TimeSpan s_life = TimeSpan.FromSeconds(4);

    // Past half of that lifetime and well inside the whole of it.
    private static readonly TimeSpan s_halfway = TimeSpan.FromMilliseconds(2200);

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
    public async Task AListingPastHalfItsLifeIsFetchedAgainBehindTheAnswer()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");

        DirectoryCache cache = Cache(store, Off, s_life);

        DirectoryListing first = await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await Task.Delay(s_halfway, TestContext.Current.CancellationToken);

        DirectoryListing again = await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        // Answered out of what is held. What the answer sets off is nobody's to wait for,
        // and by the question after it the listing is fresh again.
        Assert.Same(first.Entries, again.Entries);

        await WaitFor(store, 2);

        Assert.Equal<string>(["/music", "/music"], store.Listed);
    }

    [Fact]
    public async Task AListingInsideHalfItsLifeIsLeftAlone()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");

        DirectoryCache cache = Cache(store, Off, s_life);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);
        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await Task.Delay(s_brief, TestContext.Current.CancellationToken);

        Assert.Equal<string>(["/music"], store.Listed);
    }

    [Fact]
    public async Task ADirectoryNobodyAsksAboutIsNotRenewed()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");

        // A brief lifetime rather than s_life, because this one waits past the end of it and
        // has no mark above to stay under.
        DirectoryCache cache = Cache(store, Off, s_brief);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        // Past the whole lifetime rather than half of it: what arms a renewal is an answer,
        // so a mount nobody looks at sends nothing at all.
        await Task.Delay(s_brief + s_brief, TestContext.Current.CancellationToken);

        Assert.Equal<string>(["/music"], store.Listed);
    }

    [Fact]
    public async Task ANameAnsweredOutOfWhatIsHeldRenewsTheListing()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");

        DirectoryCache cache = Cache(store, Off, s_life);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await Task.Delay(s_halfway, TestContext.Current.CancellationToken);

        // A name that is not in the listing is answered out of it, which is an answer like
        // any other and arms the renewal like any other.
        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/nothing", TestContext.Current.CancellationToken));

        await WaitFor(store, 2);

        Assert.Equal<string>(["/music", "/music"], store.Listed);
    }

    [Fact]
    public async Task ARenewalOnItsWayIsNotSentAgain()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");

        DirectoryCache cache = Cache(store, Off, s_life);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await Task.Delay(s_halfway, TestContext.Current.CancellationToken);

        // The renewal is held on the wire, which is the window every answer after it falls
        // into: what is held still answers, and none of those answers sends a second one.
        store.Hold("/music");

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await WaitFor(store, 2);

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await Task.Delay(s_brief, TestContext.Current.CancellationToken);

        int listed = store.Listed.Count;

        store.Release();

        Assert.Equal(2, listed);
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
    public async Task ANameThisPutThereIsAskedForAgain()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/photos", "v2");
        store.AddDirectory("/films", "v3");

        DirectoryCache cache = Cache(store, new DirectorySettings { Depth = 0, Probes = 2 });

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/desktop.ini", TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/photos/desktop.ini", TestContext.Current.CancellationToken));

        // The name Windows looks for in every directory is the one that burns, and the one a
        // program then writes. What was put there is there, and the count taken before it was
        // says nothing about it.
        await cache.WriteAsync(
            "/films/desktop.ini",
            Stream.Null,
            cancellationToken: TestContext.Current.CancellationToken);

        RemoteEntry entry = await cache.GetAsync("/films/desktop.ini", TestContext.Current.CancellationToken);

        Assert.Equal("/films/desktop.ini", entry.Path);
        Assert.Empty(store.Asked);
        Assert.Equal<string>(["/music", "/photos", "/films"], store.Listed);
    }

    [Fact]
    public async Task ABurnedNameThatTurnsUpInAListingIsAskedForAgain()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/photos", "v2");
        store.AddDirectory("/films", "v3");
        store.AddFile("/films/desktop.ini");

        DirectoryCache cache = Cache(store, new DirectorySettings { Depth = 0, Probes = 2 });

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/desktop.ini", TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/photos/desktop.ini", TestContext.Current.CancellationToken));

        // Somebody opens the directory the name is in, and the listing settles it.
        await cache.ListAsync("/films", TestContext.Current.CancellationToken);

        Assert.Equal(
            "/films/desktop.ini",
            (await cache.GetAsync("/films/desktop.ini", TestContext.Current.CancellationToken)).Path);

        // A write into that directory takes the listing away, which is what happens in the
        // ordinary course of things. What is asked after it has to reach the store again
        // rather than be answered out of a count that a listing has disproved.
        await cache.WriteAsync(
            "/films/notes.txt",
            Stream.Null,
            cancellationToken: TestContext.Current.CancellationToken);

        RemoteEntry entry = await cache.GetAsync("/films/desktop.ini", TestContext.Current.CancellationToken);

        Assert.Equal("/films/desktop.ini", entry.Path);
        Assert.Empty(store.Asked);
        Assert.Equal<string>(["/music", "/photos", "/films", "/films"], store.Listed);
    }

    [Fact]
    public async Task ABurnedDirectoryThatWasListedIsAskedForAgain()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/photos", "v2");
        store.AddDirectory("/films", "v3");
        store.AddDirectory("/films/tmp", "v4");

        DirectoryCache cache = Cache(store, new DirectorySettings { Depth = 0, Probes = 2 });

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/tmp", TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/photos/tmp", TestContext.Current.CancellationToken));

        // A directory can be opened without the one above it ever being listed, and then what
        // says the name is there is the directory answering about itself.
        await cache.ListAsync("/films/tmp", TestContext.Current.CancellationToken);

        Assert.Equal(
            "/films/tmp",
            (await cache.GetAsync("/films/tmp", TestContext.Current.CancellationToken)).Path);

        await cache.WriteAsync(
            "/films/tmp/notes.txt",
            Stream.Null,
            cancellationToken: TestContext.Current.CancellationToken);

        RemoteEntry entry = await cache.GetAsync("/films/tmp", TestContext.Current.CancellationToken);

        Assert.Equal("/films/tmp", entry.Path);
        Assert.True(entry.IsDirectory);
        Assert.Empty(store.Asked);
        Assert.Equal<string>(["/music", "/photos", "/films/tmp", "/films"], store.Listed);
    }

    [Fact]
    public async Task AnAbsentNameIsCountedWithTheListingsItBought()
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

        // Counted per name and not per path: three directories, one entry. The third answer
        // bought nothing, because the second directory burned the name.
        AbsentName absent = Assert.Single(cache.Absences());

        Assert.Equal(".git", absent.Name);
        Assert.Equal(3, absent.Asked);
        Assert.Equal(2, absent.Listings);
    }

    [Fact]
    public async Task ANameAnsweredFromAListingThatIsHeldBuysNothing()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");

        DirectoryCache cache = Cache(store, new DirectorySettings { Depth = 0, Probes = 0 });

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/.git", TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ProviderException>(
            () => cache.GetAsync("/music/HEAD", TestContext.Current.CancellationToken));

        IReadOnlyList<AbsentName> absences = cache.Absences();

        // The dearest first, which is the one that paid for the listing the other was
        // answered out of.
        Assert.Equal(2, absences.Count);
        Assert.Equal(".git", absences[0].Name);
        Assert.Equal(1, absences[0].Listings);
        Assert.Equal("HEAD", absences[1].Name);
        Assert.Equal(0, absences[1].Listings);
        Assert.Equal<string>(["/music"], store.Listed);
    }

    [Fact]
    public async Task ANameThatIsThereIsNotCounted()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddFile("/music/track.mp3");

        DirectoryCache cache = Cache(store, Off);

        await cache.GetAsync("/music/track.mp3", TestContext.Current.CancellationToken);

        Assert.Empty(cache.Absences());
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

        await Open(cache, "/music");

        await WaitFor(store, 3);

        // And the level below them is not touched: one level is what was asked for.
        Assert.Equal<string>(["/music", "/music/live", "/music/studio"], store.Listed.Order());

        // What the person opens next is there already, and costs nothing.
        await Open(cache, "/music/live");

        Assert.Equal(3, store.Listed.Count);
    }

    [Fact]
    public async Task ADirectoryThatWasReadAheadReadsAheadWhenItIsOpened()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");
        store.AddDirectory("/music/live/2026", "v3");

        DirectoryCache cache = Cache(store, new DirectorySettings());

        await Open(cache, "/music");

        await WaitFor(store, 2);
        await Task.Delay(s_brief, TestContext.Current.CancellationToken);

        // Answered out of what the round before it fetched, and the level below it fetched
        // all the same. A round that ended where it succeeded left the reader with nothing
        // ahead of him from the first directory it had reached.
        await Open(cache, "/music/live");

        await WaitFor(store, 3);

        Assert.Equal<string>(["/music", "/music/live", "/music/live/2026"], store.Listed.Order());
    }

    [Fact]
    public async Task AskingWhatADirectoryIsReadsNothingAhead()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");
        store.AddDirectory("/music/live/2026", "v3");

        DirectoryCache cache = Cache(store, new DirectorySettings());

        await Open(cache, "/music");

        await WaitFor(store, 2);
        await Task.Delay(s_brief, TestContext.Current.CancellationToken);

        RemoteEntry self = await cache.GetAsync("/music/live", TestContext.Current.CancellationToken);

        await Task.Delay(s_brief, TestContext.Current.CancellationToken);

        // Anything that walks a drive asks what a directory is for every directory it passes,
        // and it asks that of the ones read ahead as well. A round started there would read
        // ahead of the walker rather than of the reader, and every round it started would arm
        // the next one, down to the last branch of the tree.
        Assert.Equal("/music/live", self.Path);
        Assert.Equal<string>(["/music", "/music/live"], store.Listed.Order());
    }

    [Fact]
    public async Task ANewRoundTakesThePlaceOfTheOneBeforeIt()
    {
        TreeStore store = new();

        store.AddDirectory("/a", "v1");
        store.AddDirectory("/a/one", "v2");
        store.AddDirectory("/a/two", "v3");
        store.AddDirectory("/b", "v4");
        store.AddDirectory("/b/one", "v5");

        DirectoryCache cache = Cache(store, new DirectorySettings());

        // The round stops on its first directory, so the second is still waiting when the
        // next directory is opened.
        store.Hold("/a/one");

        await Open(cache, "/a");

        await WaitFor(store, 2);

        await Open(cache, "/b");

        store.Release();

        await WaitFor(store, 4);
        await Task.Delay(s_brief, TestContext.Current.CancellationToken);

        // Where he is now is where the requests go, and where he was is not worth one any
        // more. Only somebody standing in a directory begins a round at all, so the round
        // this takes the place of was his as well and nobody else's is taken away.
        Assert.Equal<string>(["/a", "/a/one", "/b", "/b/one"], store.Listed);
    }

    [Fact]
    public async Task ARoundReachesItsSecondLevelBeforeTheRestOfItsFirst()
    {
        TreeStore store = new();

        store.AddDirectory("/a", "v1");
        store.AddDirectory("/a/one", "v2");
        store.AddDirectory("/a/one/deep", "v3");
        store.AddDirectory("/a/two", "v4");

        DirectoryCache cache = Cache(store, new DirectorySettings { Depth = 2 });

        await Open(cache, "/a");

        await WaitFor(store, 4);
        await Task.Delay(s_brief, TestContext.Current.CancellationToken);

        // What is below the first directory of the round goes in front of the rest of the
        // level it came from. A second level is always behind a whole first one, so a round
        // that queued it at the back would reach it last or not at all.
        Assert.Equal<string>(["/a", "/a/one", "/a/one/deep", "/a/two"], store.Listed);
    }

    [Fact]
    public async Task AWindowThatAsksAgainKeepsTheRoundItAlreadyHas()
    {
        TreeStore store = new();

        store.AddDirectory("/a", "v1");
        store.AddDirectory("/a/one", "v2");
        store.AddDirectory("/a/one/first", "v3");
        store.AddDirectory("/a/one/second", "v4");
        store.AddDirectory("/a/two", "v5");

        DirectoryCache cache = Cache(store, new DirectorySettings { Depth = 2 });

        // The round stops inside the level below the first directory, so what is queued
        // there is what a listing that emptied the queue would throw away.
        store.Hold("/a/one/first");

        await Open(cache, "/a");

        await WaitFor(store, 3);

        // The same directory a moment later, which is what a window on screen asks for.
        await Open(cache, "/a");

        store.Release();

        await WaitFor(store, 5);
        await Task.Delay(s_brief, TestContext.Current.CancellationToken);

        // He has not moved, so the round has not changed either. A level below the directory
        // he is standing in cannot be queued again from where he stands, so emptying the
        // queue for him would take away what he is about to reach.
        Assert.Equal<string>(
            ["/a", "/a/one", "/a/one/first", "/a/one/second", "/a/two"],
            store.Listed.Order());
    }

    [Fact]
    public async Task AWalkThroughADirectoryReadsNothingAhead()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");

        DirectoryCache cache = Cache(store, new DirectorySettings());

        // What a walk holds while it lists: the one handle it opened, closed again as soon
        // as it has what it came for.
        cache.Handles.Enter("/music");

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await Task.Delay(s_brief, TestContext.Current.CancellationToken);

        // A round for every directory a walk passes reads ahead of the walk rather than of
        // anybody, each round arming the next, and almost nothing it fetches is asked for.
        Assert.Equal<string>(["/music"], store.Listed);
    }

    [Fact]
    public async Task HandlesOneAfterTheOtherAreNotAReader()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");

        DirectoryCache cache = Cache(store, new DirectorySettings());

        cache.Handles.Enter("/music");
        cache.Handles.Enter("/music");
        cache.Handles.Leave("/music");

        await cache.ListAsync("/music", TestContext.Current.CancellationToken);

        await Task.Delay(s_brief, TestContext.Current.CancellationToken);

        // Two opens are not two handles: a walk that passes the same directory twice adds up
        // to two without ever having held two at once, so what is counted is what is open.
        Assert.Equal<string>(["/music"], store.Listed);
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

        await Open(cache, "/music");

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

        await Open(cache, "/music");

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

        await Open(cache, "/music");

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

        await Open(cache, "/music");

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

        await Open(cache, "/music");

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

        await Open(cache, "/music");

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

        await Open(cache, "/music");

        await WaitFor(store, 2);
        await Task.Delay(s_brief, TestContext.Current.CancellationToken);

        // The one that was asked for, and one refusal. Answering a refusal by asking nine
        // more times is how a shared server ends up with a rule against the program.
        Assert.Equal(2, store.Listed.Count);
    }

    // What a round is written down as. On the wire a listing read ahead and a listing somebody
    // waited for are the same PROPFIND, so which round a request belongs to and where that
    // round ended is said here or nowhere.
    [Fact]
    public async Task ARoundSaysWhatItQueuedAndWhereItEnded()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");
        store.AddDirectory("/music/tape", "v3");

        Recorder log = new();
        DirectoryCache cache = Cache(store, new DirectorySettings { Requests = 8 }, log: log);

        await Open(cache, "/music");

        await WaitFor(store, 3);
        await WaitFor(() => log.Written().Contains("Stopped reading ahead of /music:", StringComparison.Ordinal));

        Assert.Contains(
            "Reading ahead of /music: 2 of 2 children queued at depth 1, budget 8.",
            log.Written(),
            StringComparison.Ordinal);

        Assert.Contains("Read ahead /music/live at depth 0 in ", log.Written(), StringComparison.Ordinal);
        Assert.Contains("Read ahead /music/tape at depth 0 in ", log.Written(), StringComparison.Ordinal);

        Assert.Contains(
            "Stopped reading ahead of /music: empty, 0 in the queue.",
            log.Written(),
            StringComparison.Ordinal);
    }

    // A window asks about the directory it is showing every few seconds, and the round that
    // was begun for it is still the same round. What the second look queues is written down
    // as what it is: an opening line for every look reads as a round that ran twice, and only
    // one of the two ever ends.
    [Fact]
    public async Task ARoundTheReaderStaysInIsFilledUpRatherThanBegunAgain()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");
        store.AddDirectory("/music/tape", "v3");

        Recorder log = new();
        DirectoryCache cache = Cache(store, new DirectorySettings(), log: log);

        // The round stalls on its first child, so it is still running when the directory is
        // listed the second time.
        store.Hold("/music/live");

        await Open(cache, "/music");

        await WaitFor(store, 2);

        await Open(cache, "/music");

        Assert.Contains(
            "Reading further ahead of /music: 2 of 2 children queued at depth 1, budget 32.",
            log.Written(),
            StringComparison.Ordinal);

        Assert.Single(
            log.Written().Split('\n'),
            line => line.StartsWith("Reading ahead of /music:", StringComparison.Ordinal));

        store.Release();
    }

    [Fact]
    public async Task ARoundThatIsDroppedSaysWhatWasLeftOfIt()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");
        store.AddDirectory("/music/tape", "v3");
        store.AddDirectory("/films", "v4");

        Recorder log = new();
        DirectoryCache cache = Cache(store, new DirectorySettings(), log: log);

        // The round stalls on its first child, so its second is still waiting when the reader
        // opens something else and the round is given up.
        store.Hold("/music/live");

        await Open(cache, "/music");

        await WaitFor(store, 2);

        await Open(cache, "/films");

        Assert.Contains(
            "Stopped reading ahead of /music: dropped, 1 in the queue.",
            log.Written(),
            StringComparison.Ordinal);

        store.Release();
    }

    // A round whose queue has run dry while its last child is on the wire is still a round the
    // reader can leave, and leaving it is what ends it. Ended by what is waiting rather than by
    // what was written down, it stays open, and the next end that is written is given to it.
    [Fact]
    public async Task ARoundGivenUpWithNothingWaitingEndsWhereItWasBegun()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");
        store.AddDirectory("/films", "v3");

        Recorder log = new();
        DirectoryCache cache = Cache(store, new DirectorySettings(), log: log);

        // One child, and it is on the wire: the queue behind it is empty when he moves.
        store.Hold("/music/live");

        await Open(cache, "/music");

        await WaitFor(store, 2);

        await Open(cache, "/films");

        Assert.Contains(
            "Stopped reading ahead of /music: dropped, 0 in the queue.",
            log.Written(),
            StringComparison.Ordinal);

        store.Release();
    }

    // The directory somebody moves into may have nothing to queue, and a round that was never
    // begun cannot end. The loop of the round before it runs dry a moment later, and its end
    // belongs to the directory that round was begun in and not to where he stands by then.
    [Fact]
    public async Task ADirectoryThatQueuesNothingIsGivenNoEnd()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");
        store.AddDirectory("/films", "v3");
        store.AddDirectory("/shows", "v4");
        store.AddDirectory("/shows/late", "v5");

        Recorder log = new();
        DirectoryCache cache = Cache(store, new DirectorySettings(), log: log);

        store.Hold("/music/live");

        await Open(cache, "/music");

        await WaitFor(store, 2);

        await Open(cache, "/films");

        store.Release();

        await WaitFor(() => log.Written().Contains("Read ahead /music/live", StringComparison.Ordinal));

        // A round that ends after the empty queue of the one before it, so that what is
        // missing above is missing because it was never written and not because nothing has
        // run yet.
        await Open(cache, "/shows");

        await WaitFor(() => log.Written().Contains(
            "Stopped reading ahead of /shows: empty,",
            StringComparison.Ordinal));

        Assert.DoesNotContain(
            "Stopped reading ahead of /films",
            log.Written(),
            StringComparison.Ordinal);
    }

    // The ordinary way a round is not the first one: a directory with more children than a
    // round may spend requests on is left half read, and the reader who goes into it again
    // pays for the rest. Without the wording that says so, the log shows a round of the same
    // directory over and over and nothing that tells them apart.
    [Fact]
    public async Task ADirectoryAnEarlierRoundLeftPartOfIsReadFurtherAhead()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");
        store.AddDirectory("/music/tape", "v3");
        store.AddDirectory("/films", "v4");
        store.AddDirectory("/films/one", "v5");

        Recorder log = new();

        // One request a round, so the first round over the two children reaches one of them.
        DirectoryCache cache = Cache(store, new DirectorySettings { Requests = 1 }, log: log);

        await Open(cache, "/music");

        await WaitFor(() => log.Written().Contains(
            "Stopped reading ahead of /music: empty,",
            StringComparison.Ordinal));

        // Away and back, so that what he comes back to is a round of its own rather than the
        // one he was in.
        await Open(cache, "/films");

        await Open(cache, "/music");

        Assert.Contains(
            "Reading further ahead of /music: 1 of 2 children queued at depth 1, budget 1.",
            log.Written(),
            StringComparison.Ordinal);
    }

    // The reader stands where everything below him is already held, which is what a round
    // that has done its work leaves behind and what a prefetch that has stopped working
    // leaves behind too. Without a line the two are the same silence.
    [Fact]
    public async Task ALookWithNothingToQueueSaysSo()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");
        store.AddFile("/music/cover.jpg");

        Recorder log = new();
        DirectoryCache cache = Cache(store, new DirectorySettings(), log: log);

        await Open(cache, "/music");

        await WaitFor(() => log.Written().Contains(
            "Stopped reading ahead of /music: empty,",
            StringComparison.Ordinal));

        // The same directory again, as a window asking about the one it shows: the child is
        // held by now and the other entry is a file, so there is nothing left to queue.
        await Open(cache, "/music");

        Assert.Contains(
            "Nothing to read ahead of /music: its 2 children are files or held.",
            log.Written(),
            StringComparison.Ordinal);
    }

    // A level the round queued for itself is not a directory anybody is looking at, and one
    // of those with nothing below it would write a line behind every child of every round.
    [Fact]
    public async Task ALevelBelowTheReaderThatQueuesNothingSaysNothing()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");
        store.AddFile("/music/live/one.mp3");

        Recorder log = new();
        DirectoryCache cache = Cache(store, new DirectorySettings { Depth = 2 }, log: log);

        await Open(cache, "/music");

        await WaitFor(() => log.Written().Contains("Read ahead /music/live", StringComparison.Ordinal));

        Assert.DoesNotContain(
            "Nothing to read ahead of /music/live",
            log.Written(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChildFetchedWhileTheRoundWaitedIsWrittenDownAsSkipped()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");
        store.AddDirectory("/music/tape", "v3");

        Recorder log = new();
        DirectoryCache cache = Cache(store, new DirectorySettings(), log: log);

        store.Hold("/music/live");

        await Open(cache, "/music");

        await WaitFor(store, 2);

        // Asked for while the round is still on the first child. It is held by the time the
        // round reaches it, and the round sends nothing for it: the one end of a round that
        // costs nothing, which without this line looks like a round that never ran.
        await cache.ListAsync("/music/tape", TestContext.Current.CancellationToken);

        store.Release();

        await WaitFor(() => log.Written().Contains("Skipped /music/tape: held.", StringComparison.Ordinal));

        Assert.Contains("Skipped /music/tape: held.", log.Written(), StringComparison.Ordinal);
        Assert.Equal<string>(["/music", "/music/live", "/music/tape"], store.Listed);
    }

    [Fact]
    public async Task ARoundThatWasRefusedSaysWhatItGaveUp()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");

        for (int number = 0; number < 10; number++)
        {
            store.AddDirectory($"/music/{number}", $"v{number}");
        }

        store.RefuseBelow("/music");

        Recorder log = new();
        DirectoryCache cache = Cache(store, new DirectorySettings(), log: log);

        await Open(cache, "/music");

        await WaitFor(() => log.Written().Contains("Stopped reading ahead of /music:", StringComparison.Ordinal));

        // The nine that were never asked for are what the refusal saved, and a log that ends
        // at the refusal says nothing about them.
        Assert.Contains(
            "Stopped reading ahead of /music: busy, 9 in the queue.",
            log.Written(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARoundThatHasSpentItsBudgetSaysSo()
    {
        TreeStore store = new();

        store.AddDirectory("/music", "v1");
        store.AddDirectory("/music/live", "v2");
        store.AddDirectory("/music/tape", "v3");
        store.AddDirectory("/music/live/one", "v4");
        store.AddDirectory("/music/live/two", "v5");

        Recorder log = new();

        // Two levels into three requests' worth of directories at two requests a round: the
        // second level queues past what the budget can pay for.
        DirectoryCache cache = Cache(store, new DirectorySettings { Depth = 2, Requests = 2 }, log: log);

        await Open(cache, "/music");

        await WaitFor(() => log.Written().Contains("Stopped reading ahead of /music:", StringComparison.Ordinal));

        Assert.Contains(
            "Stopped reading ahead of /music: spent, 0 in the queue.",
            log.Written(),
            StringComparison.Ordinal);
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
                stopping: TestContext.Current.CancellationToken));

        Assert.Same(store, DirectoryCache.Over(
                store,
                TimeSpan.Zero,
                new DirectorySettings(),
                Gate(),
                stopping: TestContext.Current.CancellationToken));
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

        await Open(cache, "/music");

        await WaitFor(store, 2);

        Task<DirectoryListing> opened = Open(cache, "/music/live");

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

    private static DirectoryCache Cache(
        TreeStore store,
        DirectorySettings settings,
        TimeSpan? lifetime = null,
        ILogger? log = null) =>
        new(store, lifetime ?? s_ample, settings, Gate(), log: log);

    // A directory somebody is standing in: two handles at once, which is what a window that
    // keeps a directory open while it shows it produces and what a walk passing through does
    // not. Below that a listing begins no round, so every test about reading ahead opens the
    // directory rather than only listing it.
    private static Task<DirectoryListing> Open(DirectoryCache cache, string path)
    {
        cache.Handles.Enter(path);
        cache.Handles.Enter(path);

        return cache.ListAsync(path, TestContext.Current.CancellationToken);
    }

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

    // What the store says about a round, which is written at a level nothing is on by
    // default. Read while the round is still running, so what comes back is a copy.
    private sealed class Recorder : ILogger
    {
        private readonly List<string> _lines = [];
        private readonly Lock _sync = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (!IsEnabled(logLevel))
            {
                return;
            }

            lock (_sync)
            {
                _lines.Add(formatter(state, exception));
            }
        }

        internal string Written()
        {
            lock (_sync)
            {
                return string.Join('\n', _lines);
            }
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

        public Task<string?> CreateFileAsync(string path, CancellationToken cancellationToken = default)
        {
            _files.Add(path);

            return Task.FromResult<string?>(null);
        }

        public Task<string?> WriteAsync(
            string path,
            Stream content,
            string? ifMatch = null,
            EntryTimes times = default,
            CancellationToken cancellationToken = default)
        {
            _files.Add(path);

            return Task.FromResult<string?>(null);
        }

        public Task SetTimesAsync(string path, EntryTimes times, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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
