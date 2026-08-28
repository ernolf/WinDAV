// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Core.Configuration;

/// <summary>
/// Decides whether a configuration can be acted on, and says everything that is wrong with
/// it at once.
/// </summary>
internal static class ConfigurationValidator
{
    /// <summary>
    /// Checks a configuration and throws if it cannot be used.
    /// </summary>
    /// <param name="configuration">The configuration to check.</param>
    /// <param name="source">
    /// What to name in the message, normally the path the configuration was read from.
    /// </param>
    /// <exception cref="InvalidDataException">
    /// The configuration is unusable. The message lists every problem, not the first one:
    /// a person editing a file by hand should not have to run the program once per typo.
    /// </exception>
    public static void Validate(ClientConfiguration configuration, string source)
    {
        if (configuration.Version > ClientConfiguration.CurrentVersion)
        {
            // Nothing else is worth reporting. The fields this build does not know about
            // would all come out as errors, and every one of them would be noise.
            throw new InvalidDataException(Invariant(
                $"{source} was written by a newer build (schema version {configuration.Version}); this one reads version {ClientConfiguration.CurrentVersion}."));
        }

        List<string> problems = [];

        if (configuration.Version < 1)
        {
            problems.Add(Invariant($"version is {configuration.Version}, which is not a schema version."));
        }

        HashSet<string> accounts = CheckAccounts(configuration, problems);
        CheckMounts(configuration, accounts, problems);

        if (problems.Count > 0)
        {
            throw new InvalidDataException(
                $"{source} cannot be used:{Environment.NewLine}  - {string.Join($"{Environment.NewLine}  - ", problems)}");
        }
    }

    // The set that comes back holds what a mount names an account by, which is the identity
    // and not the name.
    private static HashSet<string> CheckAccounts(ClientConfiguration configuration, List<string> problems)
    {
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> uuids = new(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < configuration.Accounts.Count; index++)
        {
            AccountConfiguration account = configuration.Accounts[index];
            string where = Invariant($"accounts[{index}]");

            if (account.Uuid == Guid.Empty)
            {
                problems.Add($"{where} has no uuid.");
            }
            else if (!uuids.Add(account.Uuid.ToString()))
            {
                problems.Add($"{where} repeats the uuid '{account.Uuid}'.");
            }

            if (string.IsNullOrWhiteSpace(account.Id))
            {
                problems.Add($"{where} has no id.");
            }
            else if (!ids.Add(account.Id))
            {
                // Case-insensitively, because two accounts a person cannot tell apart are
                // two accounts a person will mix up.
                problems.Add($"{where} repeats the id '{account.Id}'.");
            }

            if (account.Server is null)
            {
                problems.Add($"{where} has no server.");
            }
            else if (!account.Server.IsAbsoluteUri)
            {
                problems.Add($"{where} has a relative server address, '{account.Server.OriginalString}'.");
            }
            else if (account.Server.Scheme is not ("http" or "https"))
            {
                problems.Add($"{where} has a server address of scheme '{account.Server.Scheme}', which is not HTTP.");
            }

            if (string.IsNullOrWhiteSpace(account.Provider))
            {
                problems.Add($"{where} names no provider.");
            }
        }

        return uuids;
    }

    private static void CheckMounts(
        ClientConfiguration configuration,
        HashSet<string> accountUuids,
        List<string> problems)
    {
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < configuration.Mounts.Count; index++)
        {
            MountConfiguration mount = configuration.Mounts[index];
            string where = Invariant($"mounts[{index}]");

            if (string.IsNullOrWhiteSpace(mount.Id))
            {
                problems.Add($"{where} has no id.");
            }
            else if (!ids.Add(mount.Id))
            {
                problems.Add($"{where} repeats the id '{mount.Id}'.");
            }

            if (string.IsNullOrWhiteSpace(mount.Account))
            {
                problems.Add($"{where} names no account.");
            }
            else if (!accountUuids.Contains(mount.Account))
            {
                problems.Add($"{where} names the account '{mount.Account}', which does not exist.");
            }

            if (!mount.RemotePath.StartsWith('/'))
            {
                problems.Add($"{where} has the remote path '{mount.RemotePath}', which does not start with a slash.");
            }

            CheckMountPoint(mount, where, problems);
        }
    }

    private static void CheckMountPoint(MountConfiguration mount, string where, List<string> problems)
    {
        bool hasLetter = !string.IsNullOrWhiteSpace(mount.DriveLetter);
        bool hasDirectory = !string.IsNullOrWhiteSpace(mount.Directory);

        if (hasLetter == hasDirectory)
        {
            problems.Add(hasLetter
                ? $"{where} has both a drive letter and a directory; it can have one."
                : $"{where} has neither a drive letter nor a directory.");
        }

        if (hasLetter && (mount.DriveLetter!.Length != 1 || !char.IsAsciiLetter(mount.DriveLetter[0])))
        {
            problems.Add($"{where} has the drive letter '{mount.DriveLetter}', which is not a single letter.");
        }
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
