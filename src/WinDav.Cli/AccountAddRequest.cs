// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Core;
using WinDav.Providers.Nextcloud;

namespace WinDav.Cli;

/// <summary>
/// One account as it was asked for, before anything has been asked of the server.
/// </summary>
/// <remarks>
/// What is here is what was typed, worked out as far as it can be without a server: the id
/// and the user are settled later, because a login answers with both and neither is a thing
/// to guess. The same reason as <see cref="MountRequest"/> for keeping it apart from the
/// command: this is the part the tests can reach without a server.
/// </remarks>
internal sealed class AccountAddRequest
{
    private static readonly string[] s_options =
    [
        "--provider",
        "--id",
        "--user",
        "--anonymous",
    ];

    private AccountAddRequest()
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
    /// Gets the id the account was asked to be called, or <see langword="null"/> to derive
    /// one once the user is known.
    /// </summary>
    internal required string? Id { get; init; }

    /// <summary>
    /// Gets the login name that was given, or <see langword="null"/> when the server is to
    /// be asked for it.
    /// </summary>
    /// <remarks>
    /// The name the credential is presented under, which
    /// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#71-four-names-for-an-account-uuid-id-userid-loginid">decision 71</see> keeps apart from the name the store knows the
    /// user by. What is typed here is a login, because it is what a password was issued for.
    /// </remarks>
    internal required string? LoginId { get; init; }

    /// <summary>
    /// Gets a value indicating whether the store is reached without a credential.
    /// </summary>
    internal required bool Anonymous { get; init; }

    /// <summary>
    /// Reads an account out of a command line.
    /// </summary>
    /// <param name="line">What was typed.</param>
    /// <returns>The account that was asked for.</returns>
    /// <exception cref="UsageException">What was typed cannot be carried out as written.</exception>
    internal static AccountAddRequest Parse(CommandLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        line.EnsureOnlyKnown(s_options);

        if (line.Arguments.Count != 2)
        {
            throw new UsageException($"This command needs the address of a server, as '{ProductInfo.Slug} account add <url>'.");
        }

        Uri server = ServerAddress.Read(line.Arguments[1]);
        bool anonymous = line.Flag("--anonymous");
        string? loginId = line.Value("--user");
        string provider = line.Value("--provider") ?? NextcloudProviderFactory.ProviderName;

        if (anonymous && loginId is not null)
        {
            throw new UsageException("An account is reached either as a user or anonymously, not as both.");
        }

        bool nextcloud = string.Equals(provider, NextcloudProviderFactory.ProviderName, StringComparison.Ordinal);

        if (!anonymous && loginId is null && !nextcloud)
        {
            // The login in the browser is Nextcloud's, not WebDAV's. Sending its first
            // request to a server that knows nothing of it is asking a stranger for a token.
            throw new UsageException(
                "A WebDAV store is reached with --user, or with --anonymous. The login in the browser is Nextcloud's.");
        }

        if (anonymous && nextcloud)
        {
            // Not a rule of this program's making: a Nextcloud file path has the user in it,
            // so there is nothing an anonymous Nextcloud account could be pointed at.
            throw new UsageException(
                "A Nextcloud account is reached as a user. --anonymous fits a plain WebDAV store, with --provider webdav.");
        }

        return new AccountAddRequest
        {
            Provider = provider,
            Server = server,
            Id = line.Value("--id"),
            LoginId = loginId,
            Anonymous = anonymous,
        };
    }

    /// <summary>
    /// Works out what an account is called when it was not given a name.
    /// </summary>
    /// <param name="server">The server the account is on.</param>
    /// <param name="loginId">The name that was logged in with, once it is known.</param>
    /// <returns>The id.</returns>
    /// <remarks>
    /// The same name a mount of the whole account carries, and for the same reason: it is
    /// what a person calls the thing when asked which account they mean. Built from the login
    /// rather than from the user, because decision 71 has two logins reaching one user,
    /// and a name that told them apart nowhere would be no name at all.
    /// </remarks>
    internal static string DeriveId(Uri server, string? loginId)
    {
        ArgumentNullException.ThrowIfNull(server);

        return loginId is null ? server.Host : $"{loginId}@{server.Host}";
    }
}
