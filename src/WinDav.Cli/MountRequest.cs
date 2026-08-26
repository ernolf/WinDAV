// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Core;
using WinDav.Providers.Nextcloud;

namespace WinDav.Cli;

/// <summary>
/// One mount as it was asked for, with everything that follows from it worked out.
/// </summary>
/// <remarks>
/// The defaults are derived here and nowhere else. A default worked out where it is used is
/// a default that differs between two places as soon as there are two, and this is also what
/// the tests can reach without a driver and without a server.
/// </remarks>
internal sealed class MountRequest
{
    private const string RootPath = "/";

    private static readonly string[] s_options =
        ["--provider", "--user", "--anonymous", "--path", "--mount", "--label", "--prefix", "--local"];

    private MountRequest()
    {
    }

    /// <summary>
    /// Gets the kind of store, by the name a provider is registered under.
    /// </summary>
    internal required string Provider { get; init; }

    /// <summary>
    /// Gets the server, as it would be typed into a browser.
    /// </summary>
    internal required Uri Server { get; init; }

    /// <summary>
    /// Gets the login name, or <see langword="null"/> for a store reached without one.
    /// </summary>
    internal required string? UserId { get; init; }

    /// <summary>
    /// Gets the path on the store that becomes the root of the mount.
    /// </summary>
    internal required string RemotePath { get; init; }

    /// <summary>
    /// Gets the drive letter or directory the mount appears at, or <see langword="null"/> to
    /// leave the choice to Windows.
    /// </summary>
    internal required string? MountPoint { get; init; }

    /// <summary>
    /// Gets the name the volume answers with.
    /// </summary>
    internal required string Label { get; init; }

    /// <summary>
    /// Gets the network name in the form <c>\Server\Share</c>, or <see langword="null"/> for a
    /// mount that appears as a local disk.
    /// </summary>
    internal required string? NetworkPrefix { get; init; }

    /// <summary>
    /// Gets a value indicating whether a credential has to be asked for.
    /// </summary>
    internal required bool NeedsSecret { get; init; }

    /// <summary>
    /// Reads a mount out of a command line.
    /// </summary>
    /// <param name="line">What was typed.</param>
    /// <returns>The mount that was asked for.</returns>
    /// <exception cref="UsageException">What was typed cannot be carried out as written.</exception>
    internal static MountRequest Parse(CommandLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        line.EnsureOnlyKnown(s_options);

        Uri server = ReadServer(line.SingleArgument("the address of a server"));
        bool anonymous = line.Flag("--anonymous");
        string? userId = line.Value("--user");

        if (anonymous && userId is not null)
        {
            throw new UsageException("A mount is made either as a user or anonymously, not as both.");
        }

        if (!anonymous && userId is null)
        {
            throw new UsageException(
                "A mount needs --user, or --anonymous for a store that is reached without a credential.");
        }

        string remotePath = ReadPath(line.Value("--path"));
        bool local = line.Flag("--local");
        string? prefix = NormalisePrefix(line.Value("--prefix"));

        if (local && prefix is not null)
        {
            throw new UsageException("A mount that appears as a local disk has no network name.");
        }

        return new MountRequest
        {
            Provider = line.Value("--provider") ?? NextcloudProviderFactory.ProviderName,
            Server = server,
            UserId = userId,
            RemotePath = remotePath,
            MountPoint = line.Value("--mount"),
            Label = line.Value("--label") ?? DeriveLabel(server, userId, remotePath),
            NetworkPrefix = local ? null : prefix ?? DerivePrefix(server, userId, remotePath),
            NeedsSecret = !anonymous,
        };
    }

    private static Uri ReadServer(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? server)
            || (!string.Equals(server.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                && !string.Equals(server.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)))
        {
            throw new UsageException($"'{address}' is not an http or https address.");
        }

        return server;
    }

    private static string ReadPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return RootPath;
        }

        // Written by a person, so both kinds of slash arrive and a trailing one is common.
        // The form the rest of the program works in has neither.
        string written = path.Trim().Replace('\\', '/').TrimEnd('/');

        return written.StartsWith('/') ? written : RootPath + written;
    }

    private static string? NormalisePrefix(string? prefix)
    {
        if (prefix is null)
        {
            return null;
        }

        // A person writes the name Windows shows, with two leading backslashes. WinFsp is
        // given the same name with one, which is the form a mount carries it in.
        string written = prefix.Trim().Replace('/', '\\').TrimStart('\\');

        if (written.IndexOf('\\', StringComparison.Ordinal) <= 0)
        {
            throw new UsageException("A network name is written as \\\\server\\share.");
        }

        return "\\" + written;
    }

    // Decision 58: the name of a mount is the account at its server for a whole account, and
    // the name of the folder for anything below it.
    private static string DeriveLabel(Uri server, string? userId, string remotePath)
    {
        if (!string.Equals(remotePath, RootPath, StringComparison.Ordinal))
        {
            return LastSegment(remotePath);
        }

        return userId is null ? server.Host : $"{userId}@{server.Host}";
    }

    private static string DerivePrefix(Uri server, string? userId, string remotePath)
    {
        string share = string.Equals(remotePath, RootPath, StringComparison.Ordinal)
            ? userId ?? ProductInfo.Slug
            : LastSegment(remotePath);

        return $"\\{server.Host}\\{share}";
    }

    private static string LastSegment(string path) => path[(path.LastIndexOf('/') + 1)..];
}
