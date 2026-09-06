// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Core;

namespace WinDav.Cli;

/// <summary>
/// The account that is to be called something else, and what it is to be called.
/// </summary>
/// <remarks>
/// Kept apart from the command for the reason <see cref="AccountAddRequest"/> is: this is
/// the part that can be checked without a configuration file to read or a name to look up
/// in it.
/// </remarks>
internal sealed class AccountRenameRequest
{
    private AccountRenameRequest()
    {
    }

    /// <summary>
    /// Gets the account as it was named, by id or by uuid.
    /// </summary>
    internal required string Account { get; init; }

    /// <summary>
    /// Gets what it is to be called from now on.
    /// </summary>
    internal required string Id { get; init; }

    /// <summary>
    /// Reads a renaming out of a command line.
    /// </summary>
    /// <param name="line">What was typed.</param>
    /// <returns>The renaming that was asked for.</returns>
    /// <exception cref="UsageException">What was typed cannot be carried out as written.</exception>
    internal static AccountRenameRequest Parse(CommandLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        line.EnsureOnlyKnown([]);

        if (line.Arguments.Count != 3)
        {
            throw new UsageException(
                $"This command needs an account and a new name, as '{ProductInfo.Slug} account rename <id|uuid> <name>'.");
        }

        return new AccountRenameRequest
        {
            Account = line.Arguments[1],
            Id = AccountName.Ensure(line.Arguments[2]),
        };
    }
}
