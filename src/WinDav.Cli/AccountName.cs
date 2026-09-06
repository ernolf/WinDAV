// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Cli;

/// <summary>
/// What an account may be called here.
/// </summary>
/// <remarks>
/// One place for the rule because there are two ways to set a name — with an account, and
/// afterwards — and a name refused at one of them would be a name the other could still
/// write. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#71-four-names-for-an-account-uuid-id-userid-loginid">decision 71</see>.
/// </remarks>
internal static class AccountName
{
    /// <summary>
    /// Refuses a name that could not be used to name an account.
    /// </summary>
    /// <param name="id">What the account is to be called.</param>
    /// <returns>The name.</returns>
    /// <exception cref="UsageException">It is not one an account can be called.</exception>
    /// <remarks>
    /// An account is reached by its id or by its uuid, and the id is looked at first. A name
    /// that reads as a uuid would therefore stand in front of whatever identity it spells,
    /// and the uuid is the one name of the four that nothing may come between.
    /// </remarks>
    internal static string Ensure(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new UsageException("An account needs a name to be called by, and that one is empty.");
        }

        if (Guid.TryParse(id, out _))
        {
            throw new UsageException(
                $"'{id}' reads as a uuid, and an account is looked up by name before it is looked up by uuid. Any other name will do.");
        }

        return id;
    }
}
