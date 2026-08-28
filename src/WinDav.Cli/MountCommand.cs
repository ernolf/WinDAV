// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Abstractions;
using WinDav.Core;
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

        string? secret = request.NeedsSecret
            ? Prompt.ReadSecret($"Password for {request.UserId} at {request.Server.Host}: ")
            : null;

        using IStorageConnection connection = Providers.All().Resolve(request.Provider).Connect(
            new ProviderSettings
            {
                Server = request.Server,
                UserId = request.UserId,
                Secret = secret,
                RemotePath = request.RemotePath,
                UserAgent = $"{ProductInfo.Name}/{ProductInfo.Version}",
            });

        // One request before the drive appears, so that a wrong credential or a path that is
        // not there is a sentence here instead of an error in every window afterwards.
        RemoteEntry root = await connection.Provider.GetAsync(RemoteRoot, cancellationToken)
            .ConfigureAwait(false);

        if (!root.IsDirectory)
        {
            throw new UsageException($"'{request.RemotePath}' is a file, and a mount needs a directory.");
        }

        // The remote path went to the provider, which is rooted at it, so everything above it
        // is out of reach. Rooting the file system as well would apply it a second time.
        using ProviderMount mount = new(
            connection.Provider,
            new MountSettings
            {
                MountPoint = request.MountPoint,
                NetworkPrefix = request.NetworkPrefix,
                VolumeLabel = request.Label,
                ExplorerName = request.Label,
                IconPath = request.IconPath,
            });

        mount.Mount();

        Announce(request, mount, driver);

        await WaitAsync(cancellationToken).ConfigureAwait(false);

        Console.WriteLine("Unmounting.");

        return Program.Success;
    }

    private static void Announce(MountRequest request, ProviderMount mount, Version driver)
    {
        Console.WriteLine($"{request.Label} is on {mount.MountPoint}, read only, over WinFsp {driver}.");

        if (request.NetworkPrefix is not null)
        {
            Console.WriteLine($"It is also reached as \\{request.NetworkPrefix}.");
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
