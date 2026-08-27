// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Core.Configuration;

/// <summary>
/// Every setting there is, described.
/// </summary>
/// <remarks>
/// Written out rather than gathered by reflection: the wording is the point, and no
/// attribute reads as well as a sentence. A test walks the model and fails if a property
/// has no entry here, which is what keeps the two from drifting apart.
/// </remarks>
public static class SettingCatalogue
{
    /// <summary>
    /// Gets every setting, in the order it appears in the file.
    /// </summary>
    public static IReadOnlyList<SettingDescriptor> All { get; } =
    [
        new()
        {
            Path = "version",
            Summary = "The schema version of the file.",
            Effect = "Lets a newer build migrate an older file, and an older build refuse a newer one.",
            DefaultValue = "1",
            AllowedValues = "1",
        },
        new()
        {
            Path = "accounts",
            Summary = "The servers that are reached, one entry per server and user.",
            Effect = "An account on its own reaches nothing; a mount is what makes it visible.",
            DefaultValue = "[]",
        },
        new()
        {
            Path = "accounts[].uuid",
            Summary = "What this account is, as opposed to what it is called.",
            Effect = "A mount points at it, so that renaming an account leaves every mount of it standing. It can be given wherever an account is named on the command line.",
            DefaultValue = "none, it is made when the account is added",
            AllowedValues = "a UUID, unique among the accounts",
        },
        new()
        {
            Path = "accounts[].id",
            Summary = "The name this account is referred to by.",
            Effect = "How the account is named on the command line. Changing it changes nothing else.",
            DefaultValue = "the login and the server's host, as login@host",
            AllowedValues = "any text, unique among the accounts and compared without regard to case",
        },
        new()
        {
            Path = "accounts[].server",
            Summary = "The server's base address.",
            Effect = "Every request of this account goes to it.",
            DefaultValue = "none, it has to be given",
            AllowedValues = "an absolute http or https URL",
        },
        new()
        {
            Path = "accounts[].provider",
            Summary = "The provider that speaks to this server.",
            Effect = "Decides which capabilities beyond plain WebDAV are used.",
            DefaultValue = "none, it has to be given",
        },
        new()
        {
            Path = "accounts[].userId",
            Summary = "The user as the server knows them, which is not the display name.",
            Effect = "Completes the path a provider builds its file tree from.",
            DefaultValue = "null",
        },
        new()
        {
            Path = "accounts[].loginId",
            Summary = "The name the credential is presented under.",
            Effect = "Goes into the authentication. A server that issued the password for one spelling of a user turns it down under another.",
            DefaultValue = "null, which means the same as userId",
        },
        new()
        {
            Path = "accounts[].secretRef",
            Summary = "The key the credential is stored under.",
            Effect = "Points at the credential. The credential itself is never written to this file.",
            DefaultValue = "null",
            AllowedValues = "a key of the program's own making, which is not the id of the account",
        },
        new()
        {
            Path = "accounts[].issuedHere",
            Summary = "Whether the server handed out the credential rather than a person typing it in.",
            Effect = "Removing the account withdraws such a credential on the server without asking. One that was typed in is asked about first.",
            DefaultValue = "false",
            AllowedValues = "true or false",
        },
        new()
        {
            Path = "mounts",
            Summary = "The mounts, each of which exposes part of one account's file tree.",
            Effect = "This is what appears in Windows.",
            DefaultValue = "[]",
        },
        new()
        {
            Path = "mounts[].id",
            Summary = "The name this mount is referred to by.",
            Effect = "How the mount is named on the command line.",
            DefaultValue = "none, it has to be given",
            AllowedValues = "any text, unique among the mounts and compared without regard to case",
        },
        new()
        {
            Path = "mounts[].account",
            Summary = "The account this mount reaches, named by its uuid rather than by its id.",
            Effect = "Ties the mount to a server and a user, and holds through a rename of the account.",
            DefaultValue = "none, it has to be given",
            AllowedValues = "the uuid of an account in this file",
        },
        new()
        {
            Path = "mounts[].remotePath",
            Summary = "The path on the server that becomes the root of the mount.",
            Effect = "Everything above it stays out of sight, which is how one account becomes several mounts.",
            DefaultValue = "\"/\"",
            AllowedValues = "a path starting with a slash",
        },
        new()
        {
            Path = "mounts[].driveLetter",
            Summary = "The drive letter the mount takes.",
            Effect = "Windows has twenty-six of them and shares them with everything else on the machine.",
            DefaultValue = "null",
            AllowedValues = "a single letter, or null when a directory is given instead",
        },
        new()
        {
            Path = "mounts[].directory",
            Summary = "The empty NTFS directory the mount goes into.",
            Effect = "The way past the twenty-six letters. One of this and the drive letter has to be set, not both.",
            DefaultValue = "null",
            AllowedValues = "a path to an empty directory, or null when a drive letter is given instead",
        },
        new()
        {
            Path = "mounts[].readOnly",
            Summary = "Whether the mount refuses every write.",
            Effect = "Refused by this client before a request goes out, whatever the server would have allowed.",
            DefaultValue = "false",
        },
    ];

    /// <summary>
    /// Finds the description of one setting.
    /// </summary>
    /// <param name="path">The path as it appears in <see cref="SettingDescriptor.Path"/>.</param>
    /// <returns>The description, or <see langword="null"/> when nothing is registered under that path.</returns>
    public static SettingDescriptor? Find(string path)
    {
        foreach (SettingDescriptor descriptor in All)
        {
            if (string.Equals(descriptor.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return descriptor;
            }
        }

        return null;
    }
}
