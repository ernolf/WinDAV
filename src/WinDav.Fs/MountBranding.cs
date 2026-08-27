// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security;
using Microsoft.Win32;

namespace WinDav.Fs;

/// <summary>
/// What Explorer calls a mount and what it shows beside it, which are two entries in the
/// registry and no part of the volume.
/// </summary>
/// <remarks>
/// <para>
/// Neither is written once and then left. Explorer keeps its own copy of both and keeps them
/// in two separate caches, so one of them can be shown while the other is still the old one.
/// <see cref="Ensure"/> is therefore made to be called again and again; when there is nothing
/// to do it costs a registry read and nothing else.
/// </para>
/// <para>
/// <see cref="Remove"/> leaves nothing behind. The key a mount is kept under goes whole,
/// because it describes that mount and nothing else; the one the icon hangs on goes only if
/// the icon was all that was in it, because a drive letter outlives the mount that had it.
/// </para>
/// </remarks>
public sealed class MountBranding
{
    // Where Explorer keeps what a mount is called.
    private const string MountPoints = @"Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2";

    // Where it keeps what a drive letter looks like. This one hangs on the letter and knows
    // nothing about what is mounted there.
    private const string Drives = @"Software\Classes\Applications\Explorer.exe\Drives";

    private const string LabelValue = "_LabelFromReg";

    private const string IconKey = "DefaultIcon";

    private readonly string _mountPoints;
    private readonly string _drives;
    private readonly string? _name;
    private readonly string? _icon;

    // Whether what is written is what the shell reads. Below a key of one's own it is not,
    // and then there is nothing to tell the shell about.
    private readonly bool _visibleToShell;

    /// <summary>
    /// Initialises a new instance of the <see cref="MountBranding"/> class for a mount that
    /// is already in place.
    /// </summary>
    /// <param name="settings">The mount, for its network name and what it is to be called.</param>
    /// <param name="mountPoint">
    /// Where Windows put the mount. A drive letter is what an icon can be hung on; a mount in
    /// a directory has none, and then there is no icon to write.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    public MountBranding(MountSettings settings, string? mountPoint)
        : this(settings, mountPoint, string.Empty)
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="MountBranding"/> class below a key of
    /// one's own.
    /// </summary>
    /// <param name="settings">The mount, for its network name and what it is to be called.</param>
    /// <param name="mountPoint">Where Windows put the mount.</param>
    /// <param name="registryPrefix">
    /// A key below <c>HKEY_CURRENT_USER</c> that both paths are placed under, or an empty
    /// string for the two Explorer actually reads. What it is for is a test: writing where
    /// Explorer looks would change the shell of whoever ran it.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public MountBranding(MountSettings settings, string? mountPoint, string registryPrefix)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(registryPrefix);

        _visibleToShell = registryPrefix.Length == 0;
        _mountPoints = Below(registryPrefix, MountPoints);
        _drives = Below(registryPrefix, Drives);
        _name = Trimmed(settings.ExplorerName);
        _icon = Trimmed(settings.IconPath);

        KeyName = KeyNameOf(settings.NetworkPrefix);
        DriveLetter = mountPoint is null ? null : LetterOf(mountPoint);
    }

    /// <summary>
    /// Gets the name of the key Explorer keeps this mount under, or <see langword="null"/> for
    /// a mount that has no network name and therefore no such key.
    /// </summary>
    public string? KeyName { get; }

    /// <summary>
    /// Gets the drive letter, without its colon, or <see langword="null"/> for a mount that
    /// sits in a directory.
    /// </summary>
    public string? DriveLetter { get; }

    /// <summary>
    /// Asks whether what is in the registry is what this mount wants there.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when nothing is left to write, which is also the answer when
    /// there is nothing to write at all.
    /// </returns>
    public bool IsApplied()
    {
        bool applied = false;

        // A registry that will not be read is one that cannot be shown to hold the right
        // thing, so the answer is no.
        return Attempt(() => applied = NameIsApplied() && IconIsApplied()) && applied;
    }

    /// <summary>
    /// Writes what is missing, and tells the shell when something was written.
    /// </summary>
    /// <remarks>
    /// The reading comes first because the writing does not. Setting a value that already
    /// holds that value costs nothing in the registry, but the shell would be told to look
    /// again every few seconds for the rest of the mount.
    /// </remarks>
    public void Ensure()
    {
        if (IsApplied())
        {
            return;
        }

        // A registry that will not be written is a drive under the name Windows gave it,
        // which is a drive that works. The next tick tries again.
        if (Attempt(Write))
        {
            TellTheShell();
        }
    }

    /// <summary>
    /// Takes away what <see cref="Ensure"/> wrote, and the keys that then hold nothing.
    /// </summary>
    public void Remove()
    {
        // What stays behind when this does not work is a name and an icon for a drive that is
        // no longer there, and Explorer shows neither.
        if (Attempt(Erase))
        {
            TellTheShell();
        }
    }

    // The three ways the registry says no. None of them is a reason to bring a mount down, and
    // it is the same question at every one of these calls, so it is answered in one place.
    private static bool Attempt(Action work)
    {
        try
        {
            work();

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (SecurityException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string Below(string prefix, string path) =>
        prefix.Length == 0 ? path : $@"{prefix}\{path}";

    private static string KeyPath(string root, string leaf) => $@"{root}\{leaf}";

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Explorer's own spelling for a mount: no drive letter, two hashes in front, and a hash
    // wherever the path has a backslash. Measured at the spike, where a WinFsp mount with the
    // network name \windav-spike\empty appeared as ##windav-spike#empty.
    private static string? KeyNameOf(string? networkPrefix)
    {
        string? prefix = Trimmed(networkPrefix);

        return prefix is null ? null : "##" + prefix.Trim('\\').Replace('\\', '#');
    }

    private static string? LetterOf(string mountPoint)
    {
        if (mountPoint.Length != 2 || !char.IsAsciiLetter(mountPoint[0]) || mountPoint[1] != ':')
        {
            return null;
        }

        return mountPoint[..1].ToUpperInvariant();
    }

    private void TellTheShell()
    {
        if (_visibleToShell)
        {
            ShellNotification.DrivesChanged();
        }
    }

    private bool NameIsApplied()
    {
        if (KeyName is null || _name is null)
        {
            return true;
        }

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath(_mountPoints, KeyName));

        return string.Equals(key?.GetValue(LabelValue) as string, _name, StringComparison.Ordinal);
    }

    private bool IconIsApplied()
    {
        if (DriveLetter is null)
        {
            return true;
        }

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath(KeyPath(_drives, DriveLetter), IconKey));

        // No icon wanted is a state of its own, and it is only reached once whatever was
        // written for an earlier choice is gone. Decision 58: whoever wants the ordinary
        // drive icon should have nothing of ours in their registry.
        if (_icon is null)
        {
            return key is null;
        }

        // Compared without regard to case, because a path is what it names, however it is
        // spelled.
        return string.Equals(key?.GetValue(null) as string, Reference(_icon), StringComparison.OrdinalIgnoreCase);
    }

    // The form the shell reads: a file and the index of the icon in it. An .ico holds one, at
    // zero; an .exe or a .dll may hold many, and this takes the first.
    private static string Reference(string icon) => $"{icon}, 0";

    private void Write()
    {
        WriteName();
        WriteIcon();
    }

    private void Erase()
    {
        // The whole key. It stands for this mount and for nothing else, so once the mount is
        // gone everything in it describes a drive that is not there — Explorer's own
        // _LabelFromDesktopINI among it. It makes the key again for the next mount it sees.
        if (KeyName is not null)
        {
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath(_mountPoints, KeyName), false);
        }

        RemoveIcon();
    }

    private void RemoveIcon()
    {
        if (DriveLetter is null)
        {
            return;
        }

        string drive = KeyPath(_drives, DriveLetter);

        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(drive, true))
        {
            if (key is null)
            {
                return;
            }

            key.DeleteSubKeyTree(IconKey, false);

            // Not the whole key here: a drive letter outlives the mount that held it, and what
            // else hangs on it belongs to whoever put it there. It goes only when this leaves
            // it empty, which means it was ours alone.
            if (key.ValueCount != 0 || key.SubKeyCount != 0)
            {
                return;
            }
        }

        Registry.CurrentUser.DeleteSubKey(drive, false);
    }

    private void WriteName()
    {
        if (KeyName is null || _name is null)
        {
            return;
        }

        // Created when it is missing. Explorer makes this key for a mount it has seen, so it
        // is usually there already; a mount that was made moments ago may be new to it.
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath(_mountPoints, KeyName));

        key.SetValue(LabelValue, _name, RegistryValueKind.String);
    }

    private void WriteIcon()
    {
        if (DriveLetter is null)
        {
            return;
        }

        if (_icon is null)
        {
            RemoveIcon();

            return;
        }

        using RegistryKey icon = Registry.CurrentUser.CreateSubKey(KeyPath(KeyPath(_drives, DriveLetter), IconKey));

        // The default value of the key, which is the one the shell reads.
        icon.SetValue(null, Reference(_icon), RegistryValueKind.String);
    }
}
