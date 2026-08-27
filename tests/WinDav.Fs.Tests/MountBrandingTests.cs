// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Win32;
using Xunit;

namespace WinDav.Fs.Tests;

// Everything here writes to the registry, because that is what the thing under test does.
// It writes below a key of its own, which is what the third argument of the constructor is
// for: the two keys Explorer reads belong to whoever is logged in, and a test is not entitled
// to them.
public sealed class MountBrandingTests : IDisposable
{
    private const string MountPoints = @"Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2";
    private const string Drives = @"Software\Classes\Applications\Explorer.exe\Drives";

    private readonly string _prefix = $@"Software\WinDav.Tests\{Guid.NewGuid():N}";

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_prefix, false);

        GC.SuppressFinalize(this);
    }

    // The spelling is Explorer's own, measured at the spike: no drive letter, two hashes in
    // front, one hash for every backslash.
    [Fact]
    public void TheKeyIsNamedTheWayExplorerNamesIt()
    {
        MountBranding branding = Branding(prefix: @"\global-social.net\ernolf");

        Assert.Equal("##global-social.net#ernolf", branding.KeyName);
    }

    [Fact]
    public void AMountThatIsNoNetworkDriveHasNoSuchKey()
    {
        MountBranding branding = Branding(prefix: null);

        Assert.Null(branding.KeyName);
    }

    [Theory]
    [InlineData("Z:", "Z")]
    [InlineData("z:", "Z")]
    [InlineData(null, null)]
    [InlineData(@"C:\mnt\store", null)]
    public void AnIconNeedsADriveLetterToHangOn(string? mountPoint, string? expected)
    {
        MountBranding branding = Branding(mountPoint: mountPoint);

        Assert.Equal(expected, branding.DriveLetter);
    }

    [Fact]
    public void TheNameIsWrittenWhereExplorerReadsIt()
    {
        MountBranding branding = Branding(name: "ernolf@global-social.net");

        branding.Ensure();

        Assert.Equal("ernolf@global-social.net", Label("##global-social.net#ernolf"));
    }

    [Fact]
    public void TheIconIsWrittenWithTheIndexOfTheOneToTake()
    {
        MountBranding branding = Branding(icon: @"C:\ProgramData\windav\icons\nextcloud.ico");

        branding.Ensure();

        Assert.Equal(@"C:\ProgramData\windav\icons\nextcloud.ico, 0", Icon("Z"));
    }

    // Decision 58: whoever wants the ordinary icon of a network drive should have nothing of
    // ours in their registry, and that includes what an earlier choice put there.
    [Fact]
    public void WithoutAnIconWhatAnEarlierChoiceWroteIsTakenAway()
    {
        Branding(icon: @"C:\icons\old.ico").Ensure();

        Branding(icon: null).Ensure();

        Assert.Null(Icon("Z"));
    }

    [Fact]
    public void WhatIsAlreadyWrittenIsNotWrittenAgain()
    {
        MountBranding branding = Branding();

        Assert.False(branding.IsApplied());

        branding.Ensure();

        Assert.True(branding.IsApplied());
    }

    // The whole reason the tick exists: Explorer fills its caches when it sees fit, and
    // something that took the name away has to be noticed rather than assumed not to happen.
    [Fact]
    public void ANameThatWasTakenAwayIsWrittenAgain()
    {
        MountBranding branding = Branding();

        branding.Ensure();

        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
            $@"{_prefix}\{MountPoints}\##global-social.net#ernolf", true)!)
        {
            key.DeleteValue("_LabelFromReg");
        }

        Assert.False(branding.IsApplied());

        branding.Ensure();

        Assert.True(branding.IsApplied());
    }

    // The key stands for the mount and for nothing else, Explorer's own bookkeeping in it
    // included. Once the mount is gone it describes a drive that is not there.
    [Fact]
    public void WhatIsRemovedIsTheWholeKeyOfTheMount()
    {
        MountBranding branding = Branding();

        branding.Ensure();

        // As Explorer leaves it: its own cache of the name, beside ours.
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
            $@"{_prefix}\{MountPoints}\##global-social.net#ernolf"))
        {
            key.SetValue("_LabelFromDesktopINI", string.Empty, RegistryValueKind.String);
        }

        branding.Remove();

        using RegistryKey? gone = Registry.CurrentUser.OpenSubKey(
            $@"{_prefix}\{MountPoints}\##global-social.net#ernolf");

        Assert.Null(gone);
    }

    [Fact]
    public void RemovingTakesTheIconAndTheKeyItHungOnWithIt()
    {
        MountBranding branding = Branding(icon: @"C:\icons\nextcloud.ico");

        branding.Ensure();
        branding.Remove();

        Assert.Null(Icon("Z"));

        using RegistryKey? letter = Registry.CurrentUser.OpenSubKey($@"{_prefix}\{Drives}\Z");

        Assert.Null(letter);
    }

    // A drive letter outlives the mount that had it, so what somebody else hung on it is not
    // ours to take away.
    [Fact]
    public void ADriveLetterThatCarriesSomethingElseIsKept()
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"{_prefix}\{Drives}\Z"))
        {
            key.SetValue("_LabelFromReg", "Somebody else", RegistryValueKind.String);
        }

        MountBranding branding = Branding(icon: @"C:\icons\nextcloud.ico");

        branding.Ensure();
        branding.Remove();

        using RegistryKey? letter = Registry.CurrentUser.OpenSubKey($@"{_prefix}\{Drives}\Z");

        Assert.NotNull(letter);
        Assert.Equal("Somebody else", letter.GetValue("_LabelFromReg"));
        Assert.Null(Icon("Z"));
    }

    [Fact]
    public void AMountInADirectoryIsGivenNoIconAndIsStillDone()
    {
        MountBranding branding = Branding(mountPoint: @"C:\mnt\store", icon: @"C:\icons\nextcloud.ico");

        branding.Ensure();

        Assert.True(branding.IsApplied());
        Assert.Null(Icon("Z"));
    }

    [Fact]
    public void NothingToWriteIsAlreadyDone()
    {
        MountBranding branding = Branding(name: null, icon: null, mountPoint: null, prefix: null);

        Assert.True(branding.IsApplied());
    }

    [Fact]
    public void SettingsAreRequired() =>
        Assert.Throws<ArgumentNullException>(() => new MountBranding(null!, "Z:", _prefix));

    private MountBranding Branding(
        string? name = "ernolf@global-social.net",
        string? icon = null,
        string? mountPoint = "Z:",
        string? prefix = @"\global-social.net\ernolf") =>
        new(
            new MountSettings
            {
                NetworkPrefix = prefix,
                ExplorerName = name,
                IconPath = icon,
            },
            mountPoint,
            _prefix);

    private string? Label(string keyName)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"{_prefix}\{MountPoints}\{keyName}");

        return key?.GetValue("_LabelFromReg") as string;
    }

    private string? Icon(string driveLetter)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"{_prefix}\{Drives}\{driveLetter}\DefaultIcon");

        return key?.GetValue(null) as string;
    }
}
