// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Core.Logging;
using Xunit;

namespace WinDav.Core.Tests;

public sealed class LogRedactionTests
{
    // What would be in the log if the redaction were not there. It appears nowhere else in
    // this file, so a test that passes has not been made to pass by a weaker expectation.
    private const string Secret = "kY3n7-2wQxa-Ttr91-p0Zme-vB84s";

    // The wording is Nextcloud's own, and a reader who has seen one of its reports knows what
    // it means at sight. Changing it is a decision, not an edit, so it is held here.
    [Fact]
    public void TheMarkerIsTheWordingNextcloudUses() =>
        Assert.Equal("***REMOVED SENSITIVE VALUE***", LogRedaction.Marker);

    [Fact]
    public void ACredentialInAnAddressIsTakenOutAndItsPlaceIsKept()
    {
        string written = LogRedaction.Server(new Uri($"https://ernolf:{Secret}@cloud.example/remote.php/dav"));

        Assert.Equal($"https://{LogRedaction.Marker}@cloud.example/remote.php/dav", written);
        Assert.DoesNotContain(Secret, written, StringComparison.Ordinal);
    }

    // A login flow hands its token out in the query, which is the other place a secret rides
    // along in something that looks like an ordinary address.
    [Fact]
    public void AQueryIsTakenOutWhole()
    {
        string written = LogRedaction.Server(new Uri($"https://cloud.example/login/v2/poll?token={Secret}"));

        Assert.Equal($"https://cloud.example/login/v2/poll?{LogRedaction.Marker}", written);
        Assert.DoesNotContain(Secret, written, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddressWithNothingSensitiveInItIsWrittenOut() =>
        Assert.Equal(
            "https://cloud.example/remote.php/dav/files/ernolf",
            LogRedaction.Server(new Uri("https://cloud.example/remote.php/dav/files/ernolf#top")));

    [Fact]
    public void ARelativeAddressIsLeftAlone() =>
        Assert.Equal("/remote.php/dav", LogRedaction.Server(new Uri("/remote.php/dav", UriKind.Relative)));

    [Fact]
    public void ACommandLineNamesTheProgramAndKeepsWhatIsNotAnAddress()
    {
        string written = LogRedaction.CommandLine(["mount", "--icon", @"C:\icons\cloud.ico", "--mount", "N:"]);

        Assert.Equal($@"{ProductInfo.Slug} mount --icon C:\icons\cloud.ico --mount N:", written);
    }

    // Decision 60 keeps the password off the command line, so the one way a secret gets there
    // is written into the address by hand. It is a thing people do.
    [Fact]
    public void ACommandLineHasItsAddressesRedacted()
    {
        string written = LogRedaction.CommandLine(["mount", $"https://ernolf:{Secret}@cloud.example/", "--local"]);

        Assert.Equal($"{ProductInfo.Slug} mount https://{LogRedaction.Marker}@cloud.example/ --local", written);
        Assert.DoesNotContain(Secret, written, StringComparison.Ordinal);
    }

    // By name and not by looking at what it carries: every request this product sends has a
    // password in Authorization, and a rule that has to recognise one will one day fail to.
    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("Proxy-Authorization")]
    [InlineData("WWW-Authenticate")]
    [InlineData("Proxy-Authenticate")]
    [InlineData("Cookie")]
    [InlineData("Set-Cookie")]
    public void AHeaderThatCarriesACredentialIsTakenOut(string name)
    {
        Assert.True(LogRedaction.IsSecret(name));

        string written = LogRedaction.Header(name, [$"Basic {Secret}"]);

        Assert.Equal(LogRedaction.Marker, written);
        Assert.DoesNotContain(Secret, written, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Content-Type")]
    [InlineData("Depth")]
    [InlineData("User-Agent")]
    [InlineData("")]
    [InlineData(null)]
    public void EveryOtherHeaderIsWrittenOutAsItIs(string? name)
    {
        Assert.False(LogRedaction.IsSecret(name));
        Assert.Equal("application/xml", LogRedaction.Header(name, ["application/xml"]));
    }

    // A header may be given more than once, and a record that showed only the first of them
    // would be a record that lied.
    [Fact]
    public void AHeaderGivenTwiceIsWrittenAsOne() =>
        Assert.Equal("no-cache, no-store", LogRedaction.Header("Cache-Control", ["no-cache", "no-store"]));
}
