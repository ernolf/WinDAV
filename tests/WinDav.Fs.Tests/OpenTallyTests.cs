// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Xunit;

namespace WinDav.Fs.Tests;

// Who walked the mount, counted per process. The process this runs in is the one process a
// test can name, so it is the one the naming is asserted against.
public sealed class OpenTallyTests
{
    [Fact]
    public void OpensOfOneProcessAreOneEntry()
    {
        OpenTally tally = new();
        int self = Environment.ProcessId;

        tally.Note(self, directory: true, TimeSpan.FromMilliseconds(10));
        tally.Note(self, directory: false, TimeSpan.FromMilliseconds(30));
        tally.Note(self, directory: true, TimeSpan.FromMilliseconds(20));

        Walker walker = Assert.Single(tally.Snapshot());

        Assert.Equal(self, walker.ProcessId);
        Assert.Equal(3, walker.Opened);
        Assert.Equal(2, walker.Directories);
        Assert.Equal(TimeSpan.FromMilliseconds(60), walker.Waited);
    }

    [Fact]
    public void TheProcessIsNamed()
    {
        OpenTally tally = new();

        tally.Note(Environment.ProcessId, directory: true, TimeSpan.Zero);

        using Process self = Process.GetCurrentProcess();

        Assert.Equal(self.ProcessName, Assert.Single(tally.Snapshot()).Program);
    }

    // WinFsp answers zero where it has nobody to hand through, which is every question about
    // a name that is not there.
    [Fact]
    public void AProcessThatWasNotGivenIsUnnamed()
    {
        OpenTally tally = new();

        tally.Note(0, directory: false, TimeSpan.Zero);

        Assert.Equal(OpenTally.Unnamed, Assert.Single(tally.Snapshot()).Program);
    }

    [Fact]
    public void TheOneThatOpenedTheMostComesFirst()
    {
        OpenTally tally = new();

        tally.Note(4242, directory: true, TimeSpan.Zero);
        tally.Note(4243, directory: true, TimeSpan.Zero);
        tally.Note(4243, directory: true, TimeSpan.Zero);

        IReadOnlyList<Walker> walkers = tally.Snapshot();

        Assert.Equal(2, walkers.Count);
        Assert.Equal(4243, walkers[0].ProcessId);
        Assert.Equal(4242, walkers[1].ProcessId);
    }

    [Fact]
    public void NothingWalkedIsNothingCounted() => Assert.Empty(new OpenTally().Snapshot());
}
