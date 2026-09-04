// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Xunit;

namespace WinDav.Fs.Tests;

// What is registered belongs to the machine this runs on, so what is asserted here is what
// holds on every machine: the reading does not throw, the order is the one Windows goes
// through, and the line where Windows stops loading is where it says it is.
public sealed class ShellOverlaysTests
{
    [Fact]
    public void WhatIsRegisteredCanBeRead() => Assert.NotNull(ShellOverlays.Read());

    [Fact]
    public void NoMoreAreLoadedThanWindowsLoads()
    {
        int loaded = 0;

        foreach (ShellOverlay overlay in ShellOverlays.Read())
        {
            if (overlay.Loaded)
            {
                loaded++;
            }
        }

        Assert.True(loaded <= ShellOverlays.Loads);
    }

    [Fact]
    public void EveryLoadedOneComesBeforeEveryOneThatIsNot()
    {
        bool past = false;

        foreach (ShellOverlay overlay in ShellOverlays.Read())
        {
            if (!overlay.Loaded)
            {
                past = true;
            }
            else
            {
                Assert.False(past, "A loaded handler stands behind one that is not loaded.");
            }
        }
    }

    // Ordinal, because the leading spaces vendors pad their key names with are what the sort
    // is for, and a culture-aware comparison weighs them differently.
    [Fact]
    public void TheOrderIsOrdinal()
    {
        IReadOnlyList<ShellOverlay> overlays = ShellOverlays.Read();

        for (int index = 1; index < overlays.Count; index++)
        {
            Assert.True(string.CompareOrdinal(overlays[index - 1].Name, overlays[index].Name) <= 0);
        }
    }

    // A server registered by its bare name is one Windows finds through the search path of the
    // process that loads it, and this is not that process. Only a full path can be looked for,
    // so only a full path is ever answered for.
    [Fact]
    public void OnlyAFullPathIsSaidToBeThereOrGone()
    {
        foreach (ShellOverlay overlay in ShellOverlays.Read())
        {
            if (overlay.Present is null)
            {
                continue;
            }

            Assert.True(Path.IsPathFullyQualified(Assert.IsType<string>(overlay.Module)));
        }
    }

    [Fact]
    public void EveryEntryNamesAClass()
    {
        foreach (ShellOverlay overlay in ShellOverlays.Read())
        {
            Assert.False(string.IsNullOrEmpty(overlay.Clsid));
        }
    }
}
