// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using WinDav.Abstractions;
using Xunit;

namespace WinDav.Fs.Tests;

// What a directory's times cost after the copy through it. The store underneath writes down
// every time it was asked to set, so what is asserted here is how often the server was
// troubled for a date and in which order, and that what it was given is what was kept.
public sealed class DirectoryTimesTests
{
    // Short enough to run out inside a test, long enough that a machine under load does not
    // let it run out halfway through one that is about the keeping.
    private static readonly TimeSpan s_brief = TimeSpan.FromMilliseconds(200);

    // Long enough that nothing runs out while a test is about something else.
    private static readonly TimeSpan s_ample = TimeSpan.FromMinutes(5);

    // A quiet period with room on both sides of its half, for the test that needs a wait to
    // land between two marks rather than past one: a machine running the whole suite hands a
    // 200ms delay back a second late often enough.
    private static readonly TimeSpan s_room = TimeSpan.FromSeconds(4);

    // Past half of that period and well inside the whole of it.
    private static readonly TimeSpan s_halfway = TimeSpan.FromMilliseconds(2200);

    // Several of the brief periods: long enough that whatever was going to be sent has been
    // sent, for a test that is about nothing being sent at all.
    private static readonly TimeSpan s_wellPast = TimeSpan.FromSeconds(1);

    // How long a test waits for what happens behind whoever asked. Never reached when the
    // work is done, and it is done in microseconds against a store that is a dictionary.
    private static readonly TimeSpan s_patience = TimeSpan.FromSeconds(10);

    // Two dates far enough apart to tell which of them ended up where.
    private static readonly DateTimeOffset s_created = new(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private static readonly DateTimeOffset s_modified = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void AQuietPeriodOfNothingIsNoRegisterAtAll() =>
        Assert.Null(DirectoryTimes.Over(Tree(), TimeSpan.Zero, NullLogger.Instance));

    [Fact]
    public async Task WhatADirectoryWasGivenIsNotSentWhileTheCopyIsStillGoing()
    {
        FakeStore store = Tree();

        DirectoryTimes times = new(store, s_ample, NullLogger.Instance);

        times.Given("/music", new EntryTimes(s_created, s_modified));

        await times.SendQuietAsync();

        Assert.Empty(store.TimesSet);
    }

    [Fact]
    public async Task TheTimesGoOnceNothingHasBeenWrittenForTheQuietPeriod()
    {
        FakeStore store = Tree();

        DirectoryTimes times = new(store, s_brief, NullLogger.Instance);

        times.Given("/music", new EntryTimes(s_created, s_modified));

        await WaitFor(() => store.TimesSet.Count > 0);

        Assert.Equal("/music", Assert.Single(store.TimesSet).Path);
        Assert.Equal(new EntryTimes(s_created, s_modified), store.TimesSet[0].Times);

        // Taken out of the register by the sending: one copy dates a directory once.
        await times.SendQuietAsync();

        Assert.Single(store.TimesSet);
    }

    [Fact]
    public async Task AFileThatIsStillGoingUpKeepsTheDirectoryWaiting()
    {
        FakeStore store = Tree();

        DirectoryTimes times = new(store, s_brief, NullLogger.Instance);

        times.Given("/music", new EntryTimes(s_created, s_modified));
        times.Writing("/music/track.mp3");

        // A file of a few megabytes takes longer to send than the period is long. Nothing has
        // been written yet, and the directory is about to be dated by what arrives.
        await Task.Delay(s_wellPast, TestContext.Current.CancellationToken);

        Assert.Empty(store.TimesSet);

        times.Wrote("/music/track.mp3");

        await WaitFor(() => store.TimesSet.Count > 0);

        Assert.Equal("/music", Assert.Single(store.TimesSet).Path);
    }

    [Fact]
    public async Task ADirectoryThatWasSetIsSetAgainByWhatLandsUnderItAfterwards()
    {
        FakeStore store = Tree();

        DirectoryTimes times = new(store, s_brief, NullLogger.Instance);

        times.Given("/music", new EntryTimes(s_created, s_modified));

        await WaitFor(() => store.TimesSet.Count > 0);

        // A copy that goes quiet for longer than the period and then goes on: what the
        // directory was given is kept for the life of the mount, so the file that lands after
        // the first request is answered with a second rather than with nothing at all.
        times.Wrote("/music/track.mp3");

        await WaitFor(() => store.TimesSet.Count > 1);

        Assert.Equal<string>(["/music", "/music"], store.TimesSet.ConvertAll(set => set.Path));
        Assert.Equal(new EntryTimes(s_created, s_modified), store.TimesSet[1].Times);
    }

    [Fact]
    public async Task ARequestOfOurOwnForAChildPutsTheDirectoryAboveItInAgain()
    {
        FakeStore store = Tree();

        DirectoryTimes times = new(store, s_brief, NullLogger.Instance);

        times.Given("/music", new EntryTimes(s_created, s_modified));

        await WaitFor(() => store.TimesSet.Count > 0);

        // What Windows does when it makes the next directory a while into the copy. Setting
        // its times is a write into the one above it, which has been set already, so that one
        // is set once more afterwards.
        times.Given("/music/live", new EntryTimes(s_created, s_modified));

        await WaitFor(() => store.TimesSet.Count > 2);

        Assert.Equal<string>(
            ["/music", "/music/live", "/music"],
            store.TimesSet.ConvertAll(set => set.Path));
    }

    [Fact]
    public async Task AFileLandingUnderADirectoryPutsOffTheTimesItWasGiven()
    {
        FakeStore store = Tree();

        DirectoryTimes times = new(store, s_room, NullLogger.Instance);

        times.Given("/music", new EntryTimes(s_created, s_modified));
        times.Given("/music/live", new EntryTimes(s_created, s_modified));

        await Task.Delay(s_halfway, TestContext.Current.CancellationToken);

        // One file, deep in the tree. A store that works a directory's date out from what is
        // in it has dated every directory above the file, so every one of them waits again.
        times.Wrote("/music/live/2026/track.mp3");

        await Task.Delay(s_halfway, TestContext.Current.CancellationToken);

        // Past the whole period counted from the times, and half of one counted from the file
        // that landed after them, which is the one that decides.
        Assert.Empty(store.TimesSet);

        await WaitFor(() => store.TimesSet.Count == 2);

        Assert.Equal<string>(["/music/live", "/music"], store.TimesSet.ConvertAll(set => set.Path));
    }

    [Fact]
    public void EveryDirectoryIsSetAfterWhatIsBelowItHasBeen()
    {
        FakeStore store = Tree();

        DirectoryTimes times = new(store, s_ample, NullLogger.Instance);

        times.Given("/music", new EntryTimes(s_created, s_modified));
        times.Given("/music/live", new EntryTimes(s_created, s_modified));
        times.Given("/music/live/2026", new EntryTimes(s_created, s_modified));

        times.Stop();

        // Setting a directory's times is itself a write into the one above it, so going up
        // the tree is the only order in which every one of them keeps what it was given.
        Assert.Equal<string>(
            ["/music/live/2026", "/music/live", "/music"],
            store.TimesSet.ConvertAll(set => set.Path));
    }

    [Fact]
    public void WhatWasKeptForADirectoryGoesWithTheDirectory()
    {
        FakeStore store = Tree();

        DirectoryTimes times = new(store, s_ample, NullLogger.Instance);

        times.Given("/music", new EntryTimes(s_created, s_modified));
        times.Given("/music/live", new EntryTimes(s_created, s_modified));

        times.Gone("/music");
        times.Stop();

        // Neither the name that went nor anything that stood under it: there is nothing left
        // to set a date on, and the store would answer both with 404.
        Assert.Empty(store.TimesSet);
    }

    [Fact]
    public void WhatIsStillWaitingWhenTheMountComesDownIsSentAtOnce()
    {
        FakeStore store = Tree();

        DirectoryTimes times = new(store, s_ample, NullLogger.Instance);

        times.Given("/music", new EntryTimes(s_created, s_modified));

        // Nowhere near quiet for the whole period, and there will be no period after this.
        times.Stop();

        Assert.Equal("/music", Assert.Single(store.TimesSet).Path);
    }

    [Fact]
    public void ADirectoryThatWasGivenNoTimeAtAllIsNotKept()
    {
        FakeStore store = Tree();

        DirectoryTimes times = new(store, s_ample, NullLogger.Instance);

        times.Given("/music", default);
        times.Stop();

        Assert.Empty(store.TimesSet);
    }

    [Fact]
    public void AStoreThatRefusesATimeDoesNotTakeTheMountWithIt()
    {
        FakeStore store = Tree();

        DirectoryTimes times = new(store, s_ample, NullLogger.Instance);

        times.Given("/music", new EntryTimes(s_created, s_modified));

        store.FailWith = ProviderError.PermissionDenied;

        // A date nobody asked for and nobody is waiting on. The refusal is written down and
        // gone no further with, and the mount comes down all the same.
        times.Stop();

        Assert.Empty(store.TimesSet);
    }

    private static FakeStore Tree()
    {
        FakeStore store = new();

        store.AddDirectory("/music");
        store.AddDirectory("/music/live");
        store.AddDirectory("/music/live/2026");

        return store;
    }

    // The sending happens behind whoever asked, so a test that is about it has to wait for
    // it. It is done in microseconds against a dictionary; the patience is for a machine
    // under load, and reaching the end of it is the failure the assertion afterwards reports.
    private static async Task WaitFor(Func<bool> until)
    {
        long deadline = Environment.TickCount64 + (long)s_patience.TotalMilliseconds;

        while (!until() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
    }
}
