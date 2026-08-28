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
/// the tests can reach without a driver and without a server. What the store turns out to be
/// is not known here: a mount either names an account, which holds it, or an address, whose
/// user the server is asked about. The naming therefore waits for it; decisions.md 72.
/// </remarks>
internal sealed class MountRequest
{
    private const string RootPath = "/";

    private static readonly string[] s_options =
    [
        "--account",
        "--provider",
        "--user",
        "--anonymous",
        "--path",
        "--mount",
        "--label",
        "--icon",
        "--prefix",
        "--local",
    ];

    // What the account settles, and is therefore not asked about a second time next to one.
    private static readonly string[] s_ofTheStore = ["--provider", "--user", "--anonymous"];

    private MountRequest()
    {
    }

    /// <summary>
    /// Gets the account the mount is made from, by its id or its uuid, or
    /// <see langword="null"/> when the store was typed out instead.
    /// </summary>
    internal required string? Account { get; init; }

    /// <summary>
    /// Gets the kind of store, by the name a provider is registered under, or
    /// <see langword="null"/> when the account says which it is.
    /// </summary>
    internal required string? Provider { get; init; }

    /// <summary>
    /// Gets the server, as it would be typed into a browser, or <see langword="null"/> when
    /// the account holds it.
    /// </summary>
    internal required Uri? Server { get; init; }

    /// <summary>
    /// Gets the name the credential is presented under, <see langword="null"/> for a store
    /// reached without one, and <see langword="null"/> when the account holds it.
    /// </summary>
    /// <remarks>
    /// A login, not the user the store knows: decisions.md 71 keeps the two apart, and which
    /// of them a server means by the name in a path is the server's to answer.
    /// </remarks>
    internal required string? LoginId { get; init; }

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
    /// Gets the file the drive icon is taken from, as a full path, or <see langword="null"/>
    /// for the icon Windows gives a network drive.
    /// </summary>
    internal required string? IconPath { get; init; }

    /// <summary>
    /// Gets a value indicating whether a credential has to be asked for.
    /// </summary>
    /// <remarks>
    /// Of a mount that names an account this is never true: what it would ask for is in the
    /// secret store already.
    /// </remarks>
    internal required bool NeedsSecret { get; init; }

    // What was typed about the naming, kept until there is a store to name after.
    private string? GivenLabel { get; init; }

    private string? GivenPrefix { get; init; }

    private bool Local { get; init; }

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

        bool local = line.Flag("--local");
        string? prefix = NormalisePrefix(line.Value("--prefix"));

        if (local && prefix is not null)
        {
            throw new UsageException("A mount that appears as a local disk has no network name.");
        }

        string? account = line.Value("--account");
        string? provider = null;
        Uri? server = null;
        string? loginId = null;
        bool anonymous = false;

        if (account is null)
        {
            server = ServerAddress.Read(line.SingleArgument("the address of a server, or --account"));
            anonymous = line.Flag("--anonymous");
            loginId = line.Value("--user");
            provider = line.Value("--provider") ?? NextcloudProviderFactory.ProviderName;

            if (anonymous && loginId is not null)
            {
                throw new UsageException("A mount is made either as a user or anonymously, not as both.");
            }

            if (!anonymous && loginId is null)
            {
                throw new UsageException(
                    "A mount needs --user, or --anonymous for a store that is reached without a credential.");
            }
        }
        else
        {
            EnsureTheAccountIsLeftToIt(line);
        }

        return new MountRequest
        {
            Account = account,
            Provider = provider,
            Server = server,
            LoginId = loginId,
            RemotePath = ReadPath(line.Value("--path")),
            MountPoint = line.Value("--mount"),
            IconPath = ReadIcon(line.Value("--icon")),
            NeedsSecret = account is null && !anonymous,
            GivenLabel = line.Value("--label"),
            GivenPrefix = prefix,
            Local = local,
        };
    }

    /// <summary>
    /// Works out what the drive is called, once the store it shows is known.
    /// </summary>
    /// <param name="server">The server the mount reaches.</param>
    /// <param name="userId">The user as that server knows them, or <see langword="null"/>.</param>
    /// <returns>The name the volume answers with and Explorer shows.</returns>
    internal string LabelFor(Uri server, string? userId)
    {
        ArgumentNullException.ThrowIfNull(server);

        return GivenLabel ?? DeriveLabel(server, userId, RemotePath);
    }

    /// <summary>
    /// Works out the network name, once the store it shows is known.
    /// </summary>
    /// <param name="server">The server the mount reaches.</param>
    /// <param name="userId">The user as that server knows them, or <see langword="null"/>.</param>
    /// <returns>
    /// The name in the form <c>\Server\Share</c>, or <see langword="null"/> for a mount that
    /// appears as a local disk.
    /// </returns>
    internal string? PrefixFor(Uri server, string? userId)
    {
        ArgumentNullException.ThrowIfNull(server);

        return Local ? null : GivenPrefix ?? DerivePrefix(server, userId, RemotePath);
    }

    // Decision 72: the account holds the server, the provider, the user and the credential,
    // so an option that says any of it again either agrees and means nothing, or disagrees
    // and is overruled. Neither is worth allowing.
    private static void EnsureTheAccountIsLeftToIt(CommandLine line)
    {
        if (line.Arguments.Count > 0)
        {
            throw new UsageException(
                $"A mount is made from an account or from an address, not from both, and '{line.Arguments[0]}' was read as an address.");
        }

        foreach (string named in s_ofTheStore)
        {
            if (line.Given(named))
            {
                throw new UsageException(
                    $"The account settles that, so {named} belongs to a mount that names no account.");
            }
        }
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

    private static string? ReadIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return null;
        }

        // Written as a full path, because the registry keeps it and is read again long after
        // whatever directory the command ran in has stopped mattering.
        string path = Path.GetFullPath(icon.Trim());

        if (!File.Exists(path))
        {
            throw new UsageException($"There is no file at '{path}'.");
        }

        return path;
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
    // the name of the folder for anything below it. Decision 72: the user in it is the one
    // the store knows, which is not always the one that was typed.
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
