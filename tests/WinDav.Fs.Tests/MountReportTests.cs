// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Core.Providers;
using Xunit;

namespace WinDav.Fs.Tests;

// The report is what a person reads afterwards, so what is asserted is that what went in
// stands in it and that nothing it cannot know is invented.
public sealed class MountReportTests
{
    [Fact]
    public void WhatWalkedTheMountStandsInIt()
    {
        string report = MountReport.Build(
            [new Walker(7364, "explorer", 45, 40, TimeSpan.FromMilliseconds(3204))],
            [],
            []);

        Assert.Contains("explorer (7364)", report, StringComparison.Ordinal);
        Assert.Contains("45 opened", report, StringComparison.Ordinal);
        Assert.Contains("40 of them directories", report, StringComparison.Ordinal);
        Assert.Contains("3204 ms", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentNameStandsInItWithWhatItCost()
    {
        string report = MountReport.Build([], [new AbsentName(".git", 228, 1)], []);

        Assert.Contains(".git: 228 asked, 1 listing bought", report, StringComparison.Ordinal);
    }

    // A mount that holds no listings counts nothing, and the report says so rather than
    // printing an empty section that reads as "nothing was asked for".
    [Fact]
    public void WhereNothingCountsTheNamesTheReportSaysSo()
    {
        string report = MountReport.Build([], absences: null, []);

        Assert.Contains("Not counted", report, StringComparison.Ordinal);
    }

    [Fact]
    public void NoMoreNamesArePrintedThanItPrints()
    {
        List<AbsentName> absences = [];

        for (int index = 0; index < MountReport.Names + 5; index++)
        {
            absences.Add(new AbsentName($"name{index}", 1, 0));
        }

        string report = MountReport.Build([], absences, []);

        Assert.Contains($"name{MountReport.Names - 1}", report, StringComparison.Ordinal);
        Assert.DoesNotContain($"name{MountReport.Names}:", report, StringComparison.Ordinal);
        Assert.Contains("and 5 more names: 5 asked, 0 listings bought", report, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLineWindowsStopsAtIsMarked()
    {
        List<ShellOverlay> overlays = [];

        for (int index = 0; index < ShellOverlays.Loads + 1; index++)
        {
            overlays.Add(new ShellOverlay(
                $"Handler{index}",
                "{00000000-0000-0000-0000-000000000000}",
                @"C:\handler.dll",
                "A vendor",
                Present: true,
                Loaded: index < ShellOverlays.Loads));
        }

        string report = MountReport.Build([], [], overlays);

        Assert.Contains("what follows is registered and never asked", report, StringComparison.Ordinal);
        Assert.Contains("A vendor", report, StringComparison.Ordinal);
    }

    // A handler whose library is not where the registry says it is stays in the report. It
    // is registered either way, and the gap is the finding.
    [Fact]
    public void AModuleThatIsNotThereIsReportedAsMissing()
    {
        string report = MountReport.Build(
            [],
            [],
            [
                new ShellOverlay(
                    "Handler",
                    "{00000000-0000-0000-0000-000000000000}",
                    @"C:\gone.dll",
                    null,
                    Present: false,
                    Loaded: true),
            ]);

        Assert.Contains(@"C:\gone.dll (missing)", report, StringComparison.Ordinal);
    }

    // A handler registered by its bare name is neither there nor gone as far as this process
    // can tell: Windows resolves it through the search path of whatever loads it. Saying that
    // is the report's job, guessing at a directory is not.
    [Fact]
    public void AModuleRegisteredByNameIsNeitherThereNorGone()
    {
        string report = MountReport.Build(
            [],
            [],
            [
                new ShellOverlay(
                    "Handler",
                    "{00000000-0000-0000-0000-000000000000}",
                    "handler.dll",
                    null,
                    Present: null,
                    Loaded: true),
            ]);

        Assert.Contains("handler.dll (registered by name, not by path)", report, StringComparison.Ordinal);
        Assert.DoesNotContain("(missing)", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AMountNobodyTouchedSaysSo()
    {
        string report = MountReport.Build([], [], []);

        Assert.Contains("Nobody.", report, StringComparison.Ordinal);
        Assert.Contains("Nothing.", report, StringComparison.Ordinal);
    }
}
