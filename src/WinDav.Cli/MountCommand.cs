// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Abstractions;
using WinDav.Core;
using WinDav.Core.Configuration;
using WinDav.Core.Providers;
using WinDav.Core.Security;
using WinDav.Fs;

namespace WinDav.Cli;

/// <summary>
/// Puts one store on a drive letter and keeps it there.
/// </summary>
/// <remarks>
/// The mount lasts as long as the command runs, because a mount lasts as long as the process
/// that owns it. What runs unattended is a service, and that is a later matter; this is the
/// command a person uses to see the thing work.
/// </remarks>
internal static class MountCommand
{
    private const string RemoteRoot = "/";

    /// <summary>
    /// Mounts, and waits until it is asked to stop.
    /// </summary>
    /// <param name="line">What was typed.</param>
    /// <param name="cancellationToken">Ends the mount.</param>
    /// <returns>The exit code.</returns>
    internal static async Task<int> RunAsync(CommandLine line, CancellationToken cancellationToken)
    {
        MountRequest request = MountRequest.Parse(line);

        // Asked before anything is built. Without the driver nothing here can work, and the
        // answer names the version that is there, which a failed mount would not.
        Version driver = ProviderMount.DriverVersion;

        (IStorageConnection connection, Uri server, string? userId) =
            await OpenAsync(request, cancellationToken).ConfigureAwait(false);

        using (connection)
        {
            // Decision 72: named after the store as it turned out to be, so that the drive
            // carries the user the server knows rather than the one that was typed.
            string label = request.LabelFor(server, userId);
            string? prefix = request.PrefixFor(server, userId);

            // One request before the drive appears, so that a wrong credential or a path that
            // is not there is a sentence here instead of an error in every window afterwards.
            RemoteEntry root = await connection.Provider.GetAsync(RemoteRoot, cancellationToken)
                .ConfigureAwait(false);

            if (!root.IsDirectory)
            {
                throw new UsageException($"'{request.RemotePath}' is a file, and a mount needs a directory.");
            }

            // The remote path went to the provider, which is rooted at it, so everything above
            // it is out of reach. Rooting the file system as well would apply it a second time.
            using ProviderMount mount = new(
                connection.Provider,
                new MountSettings
                {
                    MountPoint = request.MountPoint,
                    NetworkPrefix = prefix,
                    VolumeLabel = label,
                    ExplorerName = label,
                    IconPath = request.IconPath,
                });

            mount.Mount();

            Announce(label, prefix, mount, driver);

            await WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine("Unmounting.");

        return Program.Success;
    }

    // What the mount shows and what it is called after: the connection, and the store it
    // turned out to reach. Both ways of asking for a mount end here.
    private static Task<(IStorageConnection Connection, Uri Server, string? UserId)> OpenAsync(
        MountRequest request,
        CancellationToken cancellationToken) =>
        request.Account is { } account
            ? OfAccountAsync(account, request, cancellationToken)
            : TypedAsync(request, cancellationToken);

    // Decision 72: everything about the store is in the account, credential included, so this
    // is a mount that asks for nothing.
    private static async Task<(IStorageConnection Connection, Uri Server, string? UserId)> OfAccountAsync(
        string asked,
        MountRequest request,
        CancellationToken cancellationToken)
    {
        ConfigurationStore store = ConfigurationStore.Default();
        ClientConfiguration client = await store.LoadAsync(cancellationToken).ConfigureAwait(false);

        // Decision 71: by the name or by the uuid, whichever was given.
        AccountConfiguration account = client.FindAccount(asked)
            ?? throw new UsageException($"There is no account '{asked}', by that name or by that uuid.");

        if (account.Server is not { } server)
        {
            throw new UsageException($"The account '{account.Id}' has no server, so there is nothing to mount.");
        }

        IStorageConnection connection = await new AccountConnector(Providers.All(), DpapiSecretStore.Default())
            .ConnectAsync(account, request.RemotePath, cancellationToken)
            .ConfigureAwait(false);

        return (connection, server, account.UserId);
    }

    private static async Task<(IStorageConnection Connection, Uri Server, string? UserId)> TypedAsync(
        MountRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Server is not { } server || request.Provider is not { } provider)
        {
            // A mount names an account or an address, and the caller has looked for the
            // account already.
            throw new UsageException("A mount needs --account or the address of a server.");
        }

        string? loginId = request.LoginId;
        string? secret = request.NeedsSecret
            ? Prompt.ReadSecret($"Password for {loginId} at {server.Host}: ")
            : null;

        // Decision 72: what was typed is the name the credential is presented under, and the
        // name in the path is the server's own. Asking for it here is what keeps a login that
        // is spelt as an address from becoming a path that is not there.
        string? userId = loginId is not null && secret is not null && NextcloudServer.IsNextcloud(provider)
            ? await NextcloudServer.ResolveUserIdAsync(server, loginId, secret, cancellationToken)
                .ConfigureAwait(false)
            : loginId;

        IStorageConnection connection = Providers.All().Resolve(provider).Connect(
            new ProviderSettings
            {
                Server = server,
                UserId = userId,
                LoginId = loginId,
                Secret = secret,
                RemotePath = request.RemotePath,
                UserAgent = $"{ProductInfo.Name}/{ProductInfo.Version}",
            });

        return (connection, server, userId);
    }

    private static void Announce(string label, string? prefix, ProviderMount mount, Version driver)
    {
        Console.WriteLine($"{label} is on {mount.MountPoint}, read only, over WinFsp {driver}.");

        if (prefix is not null)
        {
            Console.WriteLine($"It is also reached as \\{prefix}.");
        }

        Console.WriteLine("Press Ctrl+C to take it away.");
    }

    private static async Task WaitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C is how this command ends. Nothing about it is a failure.
        }
    }
}
