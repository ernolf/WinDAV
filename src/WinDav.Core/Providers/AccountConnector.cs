// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Abstractions;
using WinDav.Core.Configuration;
using WinDav.Core.Security;

namespace WinDav.Core.Providers;

/// <summary>
/// Turns what a configuration says into a connection that can be used.
/// </summary>
/// <remarks>
/// The one path from the file to a running store: find the mount, find the account it names,
/// fetch the credential it refers to, look up the provider it asks for, and let that provider
/// build the connection. Nothing here knows what any of them are.
/// </remarks>
public sealed class AccountConnector
{
    private readonly ProviderRegistry _registry;

    private readonly ISecretStore _secrets;

    /// <summary>
    /// Initialises a new instance of the <see cref="AccountConnector"/> class.
    /// </summary>
    /// <param name="registry">The providers this build knows.</param>
    /// <param name="secrets">Where the credentials are kept.</param>
    public AccountConnector(ProviderRegistry registry, ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(secrets);

        _registry = registry;
        _secrets = secrets;
    }

    /// <summary>
    /// Connects the store one mount stands for.
    /// </summary>
    /// <param name="configuration">The configuration to read.</param>
    /// <param name="mountId">The mount to connect, matched without regard to case.</param>
    /// <param name="cancellationToken">Cancels reading the credential.</param>
    /// <returns>
    /// The connection, which the caller disposes. Nothing has been sent to the server yet.
    /// </returns>
    /// <exception cref="KeyNotFoundException">
    /// The configuration has no mount under that id.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The mount names an account that is not there, or an account names a provider this
    /// build does not have.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The account refers to a credential the secret store does not hold.
    /// </exception>
    public async Task<IStorageConnection> ConnectAsync(
        ClientConfiguration configuration,
        string mountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(mountId);

        MountConfiguration mount = Find(configuration, mountId);
        AccountConfiguration account = AccountOf(configuration, mount);

        if (account.Server is null)
        {
            throw new InvalidDataException($"The account '{account.Id}' has no server.");
        }

        string? secret = await ReadSecretAsync(account, cancellationToken).ConfigureAwait(false);

        return _registry.Resolve(account.Provider).Connect(new ProviderSettings
        {
            Server = account.Server,
            UserId = account.UserId,
            Secret = secret,
            RemotePath = mount.RemotePath,
            UserAgent = UserAgent,
        });
    }

    // How this program names itself to a server. Built from the identity in the assembly, so
    // it changes with the version without anybody remembering to change it here.
    private static string UserAgent => $"{ProductInfo.Name}/{ProductInfo.Version}";

    private static MountConfiguration Find(ClientConfiguration configuration, string mountId)
    {
        foreach (MountConfiguration mount in configuration.Mounts)
        {
            if (string.Equals(mount.Id, mountId, StringComparison.OrdinalIgnoreCase))
            {
                return mount;
            }
        }

        throw new KeyNotFoundException($"There is no mount named '{mountId}'.");
    }

    private static AccountConfiguration AccountOf(ClientConfiguration configuration, MountConfiguration mount)
    {
        foreach (AccountConfiguration account in configuration.Accounts)
        {
            if (string.Equals(account.Id, mount.Account, StringComparison.OrdinalIgnoreCase))
            {
                return account;
            }
        }

        throw new InvalidDataException(
            $"The mount '{mount.Id}' names the account '{mount.Account}', which does not exist.");
    }

    private async Task<string?> ReadSecretAsync(AccountConfiguration account, CancellationToken cancellationToken)
    {
        // No reference means a store that is reached without a credential, which plain
        // WebDAV allows. An empty answer to a reference that is there is something else: the
        // credential was expected and is gone, and sending the request unauthenticated would
        // turn that into a puzzling 401 later on.
        if (string.IsNullOrEmpty(account.SecretRef))
        {
            return null;
        }

        string? secret = await _secrets.GetAsync(account.SecretRef, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(secret))
        {
            throw new InvalidOperationException(
                $"The account '{account.Id}' refers to the credential '{account.SecretRef}', which is not in the store.");
        }

        return secret;
    }
}
