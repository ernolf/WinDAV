// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Core.Configuration;

/// <summary>
/// Everything that is configured, in one object: the accounts a server is reached with and
/// the mounts that expose them.
/// </summary>
/// <remarks>
/// An instance built from its own defaults is valid and describes a client with nothing set
/// up yet. That is the shape a first start sees, and it is deliberate: every setting has a
/// default that works without being asked for.
/// </remarks>
public sealed class ClientConfiguration
{
    /// <summary>
    /// The schema version this build writes and is able to read.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Gets the schema version of the file.
    /// </summary>
    /// <remarks>
    /// It exists so a newer build can migrate an older file, and so an older build can say
    /// so instead of misreading a newer one.
    /// </remarks>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>
    /// Gets the accounts, each of which is one server reached as one user.
    /// </summary>
    public IReadOnlyList<AccountConfiguration> Accounts { get; init; } = [];

    /// <summary>
    /// Gets the mounts, each of which exposes part of one account's file tree.
    /// </summary>
    public IReadOnlyList<MountConfiguration> Mounts { get; init; } = [];

    /// <summary>
    /// Finds an account by the name it is called or by the identity it has.
    /// </summary>
    /// <param name="asked">A name, or a uuid in any of its spellings.</param>
    /// <returns>The account, or <see langword="null"/> when there is none.</returns>
    /// <remarks>
    /// The name is looked at first, because it is what a person types; the uuid comes after,
    /// because it is what a script holds on to, being the one of the two that outlives a
    /// renaming. See decisions.md 71.
    /// </remarks>
    public AccountConfiguration? FindAccount(string asked)
    {
        ArgumentNullException.ThrowIfNull(asked);

        foreach (AccountConfiguration account in Accounts)
        {
            if (string.Equals(account.Id, asked, StringComparison.OrdinalIgnoreCase))
            {
                return account;
            }
        }

        if (!Guid.TryParse(asked, out Guid uuid))
        {
            return null;
        }

        foreach (AccountConfiguration account in Accounts)
        {
            if (account.Uuid == uuid)
            {
                return account;
            }
        }

        return null;
    }
}
