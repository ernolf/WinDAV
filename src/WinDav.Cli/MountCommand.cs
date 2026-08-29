// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;
using WinDav.Abstractions;
using WinDav.Core;
using WinDav.Core.Configuration;
using WinDav.Core.Logging;
using WinDav.Core.Providers;
using WinDav.Core.Security;
using WinDav.Fs;

namespace WinDav.Cli;

/// <summary>
/// Puts one store on a drive letter and keeps it there, and writes down the mounts that are
/// worth having again.
/// </summary>
/// <remarks>
/// The mount lasts as long as the command runs, because a mount lasts as long as the process
/// that owns it. What runs unattended is a service, and that is a later matter; this is the
/// command a person uses to see the thing work. What is written down is not made here: adding
/// a mount asks nothing of a server, and running it by its name is the same mount as the one
/// its options would have made. See decisions.md 73.
/// </remarks>
internal static class MountCommand
{
    /// <summary>Writes a mount down.</summary>
    internal const string Add = "add";

    /// <summary>Shows what is written down.</summary>
    internal const string List = "list";

    /// <summary>Takes a mount out of the configuration again.</summary>
    internal const string Remove = "remove";

    // The root of the provider, which is already rooted at the remote path of the mount.
    private const string RemoteRoot = "/";

    private const string Nothing = "-";

    /// <summary>
    /// Mounts and waits until it is asked to stop, or does one of the three things a mount in
    /// the configuration can have done to it.
    /// </summary>
    /// <param name="line">What was typed.</param>
    /// <param name="logging">Where a mount going up and coming down is written down.</param>
    /// <param name="cancellationToken">Ends the mount.</param>
    /// <returns>The exit code.</returns>
    internal static async Task<int> RunAsync(
        CommandLine line,
        ILoggerFactory logging,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);

        // Decision 73: the first word is a verb, an address or the name of a mount, and the
        // verbs are the shortest list of the three. Everything that is not one of them is a
        // mount to be made, which is what this command was before it had any verbs at all.
        string? action = line.Arguments.Count > 0 ? line.Arguments[0] : null;

        return action switch
        {
            Add => await AddAsync(line, cancellationToken).ConfigureAwait(false),
            List => await ListAsync(line, cancellationToken).ConfigureAwait(false),
            Remove => await RemoveAsync(line, cancellationToken).ConfigureAwait(false),
            _ => await MountAsync(line, logging, cancellationToken).ConfigureAwait(false),
        };
    }

    private static async Task<int> MountAsync(
        CommandLine line,
        ILoggerFactory logging,
        CancellationToken cancellationToken)
    {
        MountRequest request = MountRequest.Parse(line);

        // Asked before anything is built. Without the driver nothing here can work, and the
        // answer names the version that is there, which a failed mount would not.
        Version driver = ProviderMount.DriverVersion;

        // Read once, and only by a mount that has something to look up in it.
        ClientConfiguration? client = request.Stored is null && request.Account is null
            ? null
            : await ConfigurationStore.Default().LoadAsync(cancellationToken).ConfigureAwait(false);

        if (client is not null && request.Stored is { } stored)
        {
            request = MountRequest.OfStored(client.FindMount(stored)
                ?? throw new UsageException($"There is no mount named '{stored}'. 'mount {List}' says which there are."));
        }

        (IStorageConnection connection, Uri server, string? userId) =
            await OpenAsync(request, client, logging, cancellationToken).ConfigureAwait(false);

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

            // Written before the drive appears, so that a mount that never comes up has left
            // behind what it was trying to reach. The address is redacted: one typed with a
            // credential in it is the one way a password gets onto a command line.
            ILogger log = logging.CreateLogger(typeof(MountCommand));

            // Redacting an address is work, and a log that is switched off should not pay for
            // it (CA1873).
            if (log.IsEnabled(LogLevel.Information))
            {
                log.LogInformation(
                    "Mounting {RemotePath} of {User} at {Server} over WinFsp {Driver}.",
                    request.RemotePath,
                    userId ?? "anonymous",
                    LogRedaction.Server(server),
                    driver);
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
                },
                logging);

            mount.Mount();

            Announce(label, prefix, mount, driver);

            await WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        WriteUnmounting();

        return Program.Success;
    }

    private static async Task<int> AddAsync(CommandLine line, CancellationToken cancellationToken)
    {
        MountAddRequest request = MountAddRequest.Parse(line);

        ConfigurationStore configuration = ConfigurationStore.Default();
        ClientConfiguration client = await configuration.LoadAsync(cancellationToken).ConfigureAwait(false);

        if (client.FindMount(request.Id) is not null)
        {
            throw new UsageException($"There is already a mount named '{request.Id}'.");
        }

        // Decision 73: nothing is asked of a server here, so what can be checked is what the
        // file itself answers. That the account is there is the one thing that would make
        // this mount unrunnable, and it is worth saying now rather than at the first attempt.
        AccountConfiguration account = client.FindAccount(request.Account)
            ?? throw new UsageException($"There is no account '{request.Account}', by that name or by that uuid.");

        await configuration.SaveAsync(
            new ClientConfiguration
            {
                Version = client.Version,
                Accounts = client.Accounts,
                Mounts =
                [
                    .. client.Mounts,
                    new MountConfiguration
                    {
                        Id = request.Id,

                        // Decision 71: the identity, so that renaming the account afterwards
                        // leaves this mount standing.
                        Account = account.Uuid.ToString(),
                        RemotePath = request.RemotePath,
                        DriveLetter = request.DriveLetter,
                        Directory = request.Directory,
                        Label = request.Label,
                        IconPath = request.IconPath,
                        NetworkPrefix = request.NetworkPrefix,
                        Local = request.Local,
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);

        WriteAdded(request.Id, account.Id, configuration.FilePath);

        return Program.Success;
    }

    private static async Task<int> ListAsync(CommandLine line, CancellationToken cancellationToken)
    {
        line.EnsureOnlyKnown([]);
        EnsureNothingElse(line, List);

        ConfigurationStore configuration = ConfigurationStore.Default();
        ClientConfiguration client = await configuration.LoadAsync(cancellationToken).ConfigureAwait(false);

        if (client.Mounts.Count == 0)
        {
            WriteNoMounts(configuration.FilePath);

            return Program.Success;
        }

        List<string[]> rows = [["ID", "ACCOUNT", "PATH", "AT", "LABEL", "NETWORK", "ICON"]];

        foreach (MountConfiguration mount in client.Mounts)
        {
            rows.Add(
            [
                mount.Id,

                // The name, because that is what an account is called on the command line.
                // It is always found: a mount naming an account that is not there does not
                // get past the validator when the file is read.
                client.FindAccount(mount.Account)?.Id ?? mount.Account,
                mount.RemotePath,
                PlaceOf(mount),
                mount.Label ?? Nothing,
                NetworkOf(mount),

                // Decision 73: a state and not the path. The path is a whole line on its own,
                // and what is worth seeing here is whether the file is still where it was.
                IconStateOf(mount),
            ]);
        }

        Table.Write(rows);

        return Program.Success;
    }

    private static async Task<int> RemoveAsync(CommandLine line, CancellationToken cancellationToken)
    {
        line.EnsureOnlyKnown([]);

        if (line.Arguments.Count != 2)
        {
            throw new UsageException($"This command needs a mount, as 'mount {Remove} <mount>'.");
        }

        string asked = line.Arguments[1];

        ConfigurationStore configuration = ConfigurationStore.Default();
        ClientConfiguration client = await configuration.LoadAsync(cancellationToken).ConfigureAwait(false);

        MountConfiguration mount = client.FindMount(asked)
            ?? throw new UsageException($"There is no mount named '{asked}'.");

        await configuration.SaveAsync(
            new ClientConfiguration
            {
                Version = client.Version,
                Accounts = client.Accounts,
                Mounts = [.. client.Mounts.Where(other => other != mount)],
            },
            cancellationToken).ConfigureAwait(false);

        WriteRemoved(mount.Id);

        return Program.Success;
    }

    // What the mount shows and what it is called after: the connection, and the store it
    // turned out to reach. Every way of asking for a mount ends here.
    private static Task<(IStorageConnection Connection, Uri Server, string? UserId)> OpenAsync(
        MountRequest request,
        ClientConfiguration? client,
        ILoggerFactory logging,
        CancellationToken cancellationToken) =>
        (request.Account, client) is (string account, ClientConfiguration configured)
            ? OfAccountAsync(configured, account, request, logging, cancellationToken)
            : TypedAsync(request, logging, cancellationToken);

    // Decision 72: everything about the store is in the account, credential included, so this
    // is a mount that asks for nothing.
    private static async Task<(IStorageConnection Connection, Uri Server, string? UserId)> OfAccountAsync(
        ClientConfiguration client,
        string asked,
        MountRequest request,
        ILoggerFactory logging,
        CancellationToken cancellationToken)
    {
        // Decision 71: by the name or by the uuid, whichever was given. A stored mount gives
        // the uuid, because that is what it holds.
        AccountConfiguration account = client.FindAccount(asked)
            ?? throw new UsageException($"There is no account '{asked}', by that name or by that uuid.");

        if (account.Server is not { } server)
        {
            throw new UsageException($"The account '{account.Id}' has no server, so there is nothing to mount.");
        }

        IStorageConnection connection = await new AccountConnector(
                Providers.All(logging),
                DpapiSecretStore.Default(),
                logging)
            .ConnectAsync(account, request.RemotePath, cancellationToken)
            .ConfigureAwait(false);

        return (connection, server, account.UserId);
    }

    private static async Task<(IStorageConnection Connection, Uri Server, string? UserId)> TypedAsync(
        MountRequest request,
        ILoggerFactory logging,
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

        IStorageConnection connection = Providers.All(logging).Resolve(provider).Connect(
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

    private static void EnsureNothingElse(CommandLine line, string action)
    {
        if (line.Arguments.Count > 1)
        {
            throw new UsageException($"'mount {action}' takes nothing after it, and '{line.Arguments[1]}' was read as something.");
        }
    }

    // What a mount says about where it appears, which is nothing at all when it is content
    // with whatever letter is free at the time.
    private static string PlaceOf(MountConfiguration mount) =>
        mount.DriveLetter is { } letter ? $"{letter}:" : mount.Directory ?? "next free";

    private static string NetworkOf(MountConfiguration mount)
    {
        if (mount.Local)
        {
            return "local disk";
        }

        // Shown as a person writes it, with the two backslashes Windows puts in front of it.
        return mount.NetworkPrefix is { } prefix ? $"\\{prefix}" : Nothing;
    }

    private static string IconStateOf(MountConfiguration mount)
    {
        if (mount.IconPath is not { } path)
        {
            return "none";
        }

        // Windows reads the file whenever it shows the drive, so one that has been moved away
        // since is a mount that comes up without its icon and says nothing about why.
        return File.Exists(path) ? "set" : "missing";
    }

    // Everything written to the console is written from a method that is not async, for the
    // reason Program gives where it does the same.
    private static void Announce(string label, string? prefix, ProviderMount mount, Version driver)
    {
        Console.WriteLine($"{label} is on {mount.MountPoint}, read only, over WinFsp {driver}.");

        if (prefix is not null)
        {
            Console.WriteLine($"It is also reached as \\{prefix}.");
        }

        Console.WriteLine("Press Ctrl+C to take it away.");
    }

    private static void WriteAdded(string id, string account, string filePath) =>
        Console.WriteLine($"The mount '{id}' of the account '{account}' is in {filePath}. Run it with 'mount {id}'.");

    private static void WriteRemoved(string id) =>
        Console.WriteLine($"The mount '{id}' is gone. The account it was made from is untouched.");

    private static void WriteNoMounts(string filePath) =>
        Console.WriteLine($"There is no mount written down yet. {filePath} is where they go.");

    private static void WriteUnmounting() => Console.WriteLine("Unmounting.");

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
