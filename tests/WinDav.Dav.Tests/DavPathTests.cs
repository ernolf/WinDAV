// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Abstractions;
using Xunit;

namespace WinDav.Dav.Tests;

public sealed class DavPathTests
{
    private static readonly Uri s_base = new("https://cloud.example.com/remote.php/dav/files/ernolf/");

    // What an account reached under an address rather than an identifier looks like: the
    // program writes the "@" escaped, because that is what escaping a segment does, and the
    // server writes it plainly. Both name the same collection.
    private static readonly Uri s_address = new("https://cloud.example.com/remote.php/dav/files/ernolf.gradenwitz%40gmail.com/");

    [Fact]
    public void TheBaseItselfIsTheRoot() =>
        Assert.Equal("/", DavPath.FromHref(s_base, "/remote.php/dav/files/ernolf/"));

    [Fact]
    public void ACollectionLosesItsTrailingSlash() =>
        Assert.Equal("/holidays", DavPath.FromHref(s_base, "/remote.php/dav/files/ernolf/holidays/"));

    [Fact]
    public void AnHrefWrittenAsAWholeUriIsRead()
    {
        Assert.Equal(
            "/note.txt",
            DavPath.FromHref(s_base, "https://cloud.example.com/remote.php/dav/files/ernolf/note.txt"));
    }

    [Fact]
    public void ANameIsUnescaped() =>
        Assert.Equal("/a note.txt", DavPath.FromHref(s_base, "/remote.php/dav/files/ernolf/a%20note.txt"));

    [Fact]
    public void ASegmentEscapedOnOneSideOnlyIsStillTheSameSegment()
    {
        Assert.Equal("/", DavPath.FromHref(s_address, "/remote.php/dav/files/ernolf.gradenwitz@gmail.com/"));

        Assert.Equal(
            "/note.txt",
            DavPath.FromHref(s_address, "/remote.php/dav/files/ernolf.gradenwitz@gmail.com/note.txt"));
    }

    [Fact]
    public void ANameThatMerelyBeginsWithTheBaseIsNotBelowIt()
    {
        // Two accounts, one of which is spelled like the other with more letters after it.
        ProviderException exception = Assert.Throws<ProviderException>(
            () => DavPath.FromHref(s_base, "/remote.php/dav/files/ernolfine/note.txt"));

        Assert.Equal(ProviderError.Protocol, exception.Error);
    }

    [Fact]
    public void SomethingOutsideTheBaseIsRefused()
    {
        ProviderException exception = Assert.Throws<ProviderException>(
            () => DavPath.FromHref(s_base, "/remote.php/dav/files/someone-else/note.txt"));

        Assert.Equal(ProviderError.Protocol, exception.Error);
    }
}
