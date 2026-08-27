// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using WinDav.Abstractions;
using WinDav.Core;
using WinDav.Core.Configuration;
using WinDav.Core.Security;
using WinDav.Providers.Nextcloud;
using WinDav.Providers.Nextcloud.Login;
using WinDav.Providers.Nextcloud.Ocs;

namespace WinDav.Cli;

/// <summary>
/// Puts an account into the configuration, shows what is there, and takes one away again.
/// </summary>
/// <remarks>
/// The credential never reaches the configuration file. It goes into the secret store under a
/// key of the program's own making, and the file holds that key and nothing else; decisions.md
/// 68 says which store and why, and 70 why the key is not the name of the account.
/// </remarks>
internal static class AccountCommand
{
    private const string Add = "add";

    private const string List = "list";

    private const string Remove = "remove";

    /// <summary>
    /// Carries out one of the three things that can be done with an account.
    /// </summary>
    /// <param name="line">What was typed.</param>
    /// <param name="cancellationToken">Cancels what is under way.</param>
    /// <returns>The exit code.</returns>
    internal static async Task<int> RunAsync(CommandLine line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);

        string action = line.Arguments.Count > 0
            ? line.Arguments[0]
            : throw new UsageException($"An account is added, listed or removed: '{Add}', '{List}', '{Remove}'.");

        return action switch
        {
            Add => await AddAsync(line, cancellationToken).ConfigureAwait(false),
            List => await ListAsync(line, cancellationToken).ConfigureAwait(false),
            Remove => await RemoveAsync(line, cancellationToken).ConfigureAwait(false),
            _ => throw new UsageException($"There is no 'account {action}'. There is {Add}, {List} and {Remove}."),
        };
    }

    private static async Task<int> AddAsync(CommandLine line, CancellationToken cancellationToken)
    {
        AccountAddRequest request = AccountAddRequest.Parse(line);

        // Refused before anything is asked of a server: the name goes into the configuration,
        // and one no provider answers to is a mount that fails later for no visible reason.
        _ = Providers.All().Resolve(request.Provider);

        ConfigurationStore configuration = ConfigurationStore.Default();
        ClientConfiguration client = await configuration.LoadAsync(cancellationToken).ConfigureAwait(false);

        EnsureFree(client, request.Id);

        Uri server = request.Server;
        string? loginId = request.LoginId;
        string? userId = loginId;
        string? secret = null;
        bool issuedHere = false;

        if (!request.Anonymous)
        {
            if (loginId is null)
            {
                LoginFlowCredentials credentials = await LogInAsync(server, cancellationToken).ConfigureAwait(false);

                secret = credentials.AppPassword;
                issuedHere = true;

                // Decision 54: the spelling the server has written into the record of the
                // password it just made, and the only one it takes that password under.
                loginId = credentials.LoginName;

                // The server that answered the login is the one the credential is good for,
                // which is not always the address as it was typed.
                server = credentials.Server;
            }
            else
            {
                secret = Prompt.ReadSecret($"App password for {loginId} at {server.Host}: ");
            }

            // Decision 71: the name the file tree on the server is called after, which is a
            // different question from the name this login was accepted under.
            userId = IsNextcloud(request.Provider)
                ? await ResolveUserIdAsync(server, loginId, secret, cancellationToken).ConfigureAwait(false)
                : loginId;
        }

        // Decision 71: the same user at the same server, reached under the same login, is the
        // same account. Under another login it is another door into it, and whether that is
        // worth an account of its own is the user's to say.
        if (userId is not null && loginId is not null && SameUser(client, server, userId) is { } known)
        {
            bool anotherDoor = !string.Equals(LoginOf(known), loginId, StringComparison.OrdinalIgnoreCase);

            if (!anotherDoor || !AskAboutAnotherDoor(known, server, loginId))
            {
                // Decision 69: what the login has just handed out goes back before the
                // refusal, so that nothing is left standing on the server.
                if (issuedHere && secret is not null)
                {
                    await WithdrawAsync(server, loginId, secret, cancellationToken).ConfigureAwait(false);
                }

                throw new UsageException(anotherDoor
                    ? $"Nothing was added. The account '{known.Id}' reaches {userId} at {server.Host} already."
                    : $"The account '{known.Id}' already reaches {userId} at {server.Host}. Another way into it is a mount, not another account.");
            }
        }

        string id = request.Id ?? AccountAddRequest.DeriveId(server, loginId);

        EnsureFree(client, id);

        string? secretRef = null;

        // The credential first. An account that points at a credential which is not there is
        // worse than a credential nothing points at: one of them is a mount that fails.
        if (secret is not null)
        {
            // Decision 70: a key of its own, so that the name of an account stays free to be
            // a name and two accounts can never come to share one file.
            secretRef = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

            await Secrets().SetAsync(secretRef, secret, cancellationToken).ConfigureAwait(false);
        }

        await configuration.SaveAsync(
            new ClientConfiguration
            {
                Version = client.Version,
                Accounts =
                [
                    .. client.Accounts,
                    new AccountConfiguration
                    {
                        // Decision 71: the one name of the four that is never typed, never
                        // shown and never changed, so that a rename is a rename.
                        Uuid = Guid.NewGuid(),
                        Id = id,
                        Server = server,
                        Provider = request.Provider,
                        UserId = userId,
                        LoginId = string.Equals(loginId, userId, StringComparison.Ordinal) ? null : loginId,
                        SecretRef = secretRef,
                        IssuedHere = issuedHere,
                    },
                ],
                Mounts = client.Mounts,
            },
            cancellationToken).ConfigureAwait(false);

        WriteAdded(id, configuration.FilePath);

        return Program.Success;
    }

    private static async Task<int> ListAsync(CommandLine line, CancellationToken cancellationToken)
    {
        line.EnsureOnlyKnown([]);
        EnsureNothingElse(line, List);

        ConfigurationStore configuration = ConfigurationStore.Default();
        ClientConfiguration client = await configuration.LoadAsync(cancellationToken).ConfigureAwait(false);

        if (client.Accounts.Count == 0)
        {
            WriteNoAccounts(configuration.FilePath);

            return Program.Success;
        }

        DpapiSecretStore secrets = Secrets();
        List<string[]> rows = [["ID", "PROVIDER", "SERVER", "USER", "LOGIN", "CREDENTIAL", "UUID"]];

        foreach (AccountConfiguration account in client.Accounts)
        {
            rows.Add(
            [
                account.Id,
                account.Provider,
                account.Server?.AbsoluteUri ?? string.Empty,
                account.UserId ?? "-",

                // Decision 71: shown next to the user rather than in place of it, because the
                // two together are what makes one of two doors into one account tellable from
                // the other.
                LoginOf(account) ?? "-",
                await StateOfAsync(secrets, account, cancellationToken).ConfigureAwait(false),

                // Last, because it is the widest and the one that is read least often. It is
                // shown at all because it is what a mount in the file points at, and what
                // account remove takes besides the name. See decisions.md 71.
                account.Uuid.ToString(),
            ]);
        }

        WriteTable(rows);

        return Program.Success;
    }

    private static async Task<int> RemoveAsync(CommandLine line, CancellationToken cancellationToken)
    {
        line.EnsureOnlyKnown([]);

        if (line.Arguments.Count != 2)
        {
            throw new UsageException($"This command needs an account, as 'account {Remove} <id|uuid>'.");
        }

        string asked = line.Arguments[1];

        ConfigurationStore configuration = ConfigurationStore.Default();
        ClientConfiguration client = await configuration.LoadAsync(cancellationToken).ConfigureAwait(false);

        AccountConfiguration account = Find(client, asked)
            ?? throw new UsageException($"There is no account '{asked}', by that name or by that uuid.");

        EnsureUnused(client, account);

        // Before anything is written, because the credential is what the request is
        // authenticated with and it is still both here and good.
        await RevokeAsync(client, account, cancellationToken).ConfigureAwait(false);

        await configuration.SaveAsync(
            new ClientConfiguration
            {
                Version = client.Version,
                Accounts = [.. client.Accounts.Where(other => other != account)],
                Mounts = client.Mounts,
            },
            cancellationToken).ConfigureAwait(false);

        // After the configuration, so that a removal which stops here leaves a credential
        // nothing points at rather than an account that cannot be reached.
        if (account.SecretRef is not null)
        {
            await Secrets().RemoveAsync(account.SecretRef, cancellationToken).ConfigureAwait(false);
        }

        WriteRemoved(account.Id);

        return Program.Success;
    }

    // Named for what it is rather than for the seam it fits: choosing the store is what the
    // program that runs is for, and this one has chosen. Decisions.md 68 says which and why.
    private static DpapiSecretStore Secrets() => DpapiSecretStore.Default();

    private static bool IsNextcloud(string provider) =>
        string.Equals(provider, NextcloudProviderFactory.ProviderName, StringComparison.Ordinal);

    // Decision 71: by the name or by the identity, whichever was given. The name comes first
    // because it is what a person types; the identity is what a script holds on to, since it
    // is the one of the two that outlives a renaming.
    private static AccountConfiguration? Find(ClientConfiguration client, string asked)
    {
        AccountConfiguration? named = client.Accounts.FirstOrDefault(account =>
            string.Equals(account.Id, asked, StringComparison.OrdinalIgnoreCase));

        if (named is not null || !Guid.TryParse(asked, out Guid uuid))
        {
            return named;
        }

        return client.Accounts.FirstOrDefault(account => account.Uuid == uuid);
    }

    // Decision 71: the name this account authenticates as, which is the name it is known by
    // wherever the server draws no distinction between the two.
    private static string? LoginOf(AccountConfiguration account) => account.LoginId ?? account.UserId;

    // Decision 70, the rule NcDavTray keeps as well: one account per server and user, matched
    // without regard to case, because that is what a person means by "the same account".
    private static AccountConfiguration? SameUser(ClientConfiguration client, Uri server, string userId) =>
        client.Accounts.FirstOrDefault(account =>
            string.Equals(account.Server?.Host, server.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(account.UserId, userId, StringComparison.OrdinalIgnoreCase));

    // Decision 71: two logins into one user are two doors into one room, and the program says
    // so instead of deciding it. No answer is a no, which is what a command run from a script
    // gets: nothing was asked for twice, so nothing is added twice.
    private static bool AskAboutAnotherDoor(AccountConfiguration known, Uri server, string loginId)
    {
        Console.WriteLine(
            $"The account '{known.Id}' is the same user at {server.Host}, logged in as {LoginOf(known)}.");
        Console.WriteLine("This login reaches the same files under another name.");

        return Prompt.Confirm($"Keep it as an account of its own, logged in as {loginId}? [y/N] ");
    }

    private static void EnsureFree(ClientConfiguration client, string? id)
    {
        // Through Find, so that a name which happens to be another account's uuid is refused
        // as well: it would reach that other account on the command line.
        if (id is not null && Find(client, id) is not null)
        {
            throw new UsageException($"There is already an account named '{id}'. Another name goes in --id.");
        }
    }

    private static void EnsureUnused(ClientConfiguration client, AccountConfiguration account)
    {
        string[] mounts =
        [
            .. client.Mounts

                // By identity, not by name: decisions.md 71.
                .Where(mount => string.Equals(mount.Account, account.Uuid.ToString(), StringComparison.OrdinalIgnoreCase))
                .Select(mount => mount.Id),
        ];

        if (mounts.Length > 0)
        {
            throw new UsageException(
                $"The account '{account.Id}' is what the mount {string.Join(", ", mounts)} is made from. Take that away first.");
        }
    }

    private static void EnsureNothingElse(CommandLine line, string action)
    {
        if (line.Arguments.Count > 1)
        {
            throw new UsageException($"'account {action}' takes nothing after it, and '{line.Arguments[1]}' was read as something.");
        }
    }

    private static async Task<string> StateOfAsync(
        DpapiSecretStore secrets,
        AccountConfiguration account,
        CancellationToken cancellationToken)
    {
        if (account.SecretRef is null)
        {
            return "none";
        }

        try
        {
            return await secrets.GetAsync(account.SecretRef, cancellationToken).ConfigureAwait(false) is null
                ? "missing"
                : "stored";
        }
        catch (InvalidOperationException)
        {
            // Decision 68: the configuration roams and the credential does not, so this is
            // what an account looks like on the second machine. Said here rather than at the
            // first mount, where it would come as a surprise.
            return "unreadable";
        }
        catch (ArgumentException)
        {
            // A reference edited into the file by hand that no store could keep.
            return "unreadable";
        }
    }

    private static async Task<LoginFlowCredentials> LogInAsync(Uri server, CancellationToken cancellationToken)
    {
        using HttpClient httpClient = Anonymous();

        LoginFlowClient login = new(httpClient, server);

        try
        {
            LoginFlowStart start = await login.StartAsync(cancellationToken).ConfigureAwait(false);

            // Decision 53: the address is given out, and the browser is opened as a
            // convenience rather than as the only way in.
            AnnounceLogin(start.Login);
            OpenBrowser(start.Login);

            return await login.WaitAsync(start.Poll, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException failure)
        {
            throw AsFailure(server, failure);
        }
        catch (FormatException answer)
        {
            throw new ProviderException(ProviderError.Protocol, $"The server is {server.Host}.", answer);
        }
    }

    private static async Task<string> ResolveUserIdAsync(
        Uri server,
        string loginName,
        string secret,
        CancellationToken cancellationToken)
    {
        using HttpClient httpClient = Authenticated(loginName, secret);

        try
        {
            return await new OcsClient(httpClient, server).GetUserIdAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException failure)
        {
            throw AsFailure(server, failure);
        }
        catch (FormatException answer)
        {
            throw new ProviderException(ProviderError.Protocol, $"The server is {server.Host}.", answer);
        }
    }

    // Decision 69: what a login handed out is given back, and a server that will not take it
    // back is said out loud rather than made into a failure. The account goes either way.
    private static async Task RevokeAsync(
        ClientConfiguration client,
        AccountConfiguration account,
        CancellationToken cancellationToken)
    {
        if (!IsNextcloud(account.Provider)
            || account.Server is not { } server
            || LoginOf(account) is not { } loginId
            || account.SecretRef is not { } secretRef)
        {
            return;
        }

        DpapiSecretStore secrets = Secrets();
        string? secret = await ReadAsync(secrets, secretRef, cancellationToken).ConfigureAwait(false);

        if (secret is null)
        {
            // Only worth a word when the server has something of ours: what a person typed in
            // is theirs to withdraw, and there is nothing to withdraw it with in any case.
            if (account.IssuedHere)
            {
                WriteKept(server, "The credential could not be read here.");
            }

            return;
        }

        if (await SharedWithAsync(secrets, client, account, secret, cancellationToken).ConfigureAwait(false) is { } other)
        {
            WriteShared(server, other);

            return;
        }

        // What came out of a login goes back without asking, because it was never anything
        // but this account. What was typed in is asked about, because it is not ours to
        // withdraw; no answer is a no, which leaves the password where it was.
        if (!account.IssuedHere
            && !Prompt.Confirm($"Withdraw the app password of '{account.Id}' on {server.Host} as well? [y/N] "))
        {
            return;
        }

        await WithdrawAsync(server, loginId, secret, cancellationToken).ConfigureAwait(false);
    }

    // Decision 71: authenticated as the name the password was issued under, which the server
    // turns down under any other spelling of the same user.
    private static async Task WithdrawAsync(
        Uri server,
        string loginId,
        string secret,
        CancellationToken cancellationToken)
    {
        using HttpClient httpClient = Authenticated(loginId, secret);

        try
        {
            await new OcsClient(httpClient, server).DeleteAppPasswordAsync(cancellationToken).ConfigureAwait(false);

            WriteRevoked(server);
        }
        catch (HttpRequestException refused)
        {
            WriteKept(server, refused.Message);
        }
    }

    // Decision 69: a password that a second account is signed in with is still in use once
    // this one is gone, and withdrawing it would take that other account down as well.
    private static async Task<string?> SharedWithAsync(
        DpapiSecretStore secrets,
        ClientConfiguration client,
        AccountConfiguration account,
        string secret,
        CancellationToken cancellationToken)
    {
        foreach (AccountConfiguration other in client.Accounts)
        {
            // Only the same server, because withdrawing a password there is nothing to an
            // account elsewhere that happens to be reached with the same one. Two stores on
            // one host are read as one, which at worst keeps a password that could have gone.
            if (other == account
                || other.SecretRef is not { } secretRef
                || !string.Equals(other.Server?.Host, account.Server?.Host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? held = await ReadAsync(secrets, secretRef, cancellationToken).ConfigureAwait(false);

            if (string.Equals(held, secret, StringComparison.Ordinal))
            {
                return other.Id;
            }
        }

        return null;
    }

    // A credential that will not open here is one this machine cannot send anywhere, which
    // comes to the same thing as not having it.
    private static async Task<string?> ReadAsync(
        DpapiSecretStore secrets,
        string reference,
        CancellationToken cancellationToken)
    {
        try
        {
            return await secrets.GetAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Written by another user, or carried over from another machine.
            return null;
        }
        catch (ArgumentException)
        {
            // A reference edited into the file by hand that no store could keep.
            return null;
        }
    }

    private static HttpClient Authenticated(string loginId, string secret)
    {
        HttpClient httpClient = Anonymous();

        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{loginId}:{secret}")));

        return httpClient;
    }

    private static HttpClient Anonymous()
    {
        HttpClient httpClient = new();

        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"{ProductInfo.Name}/{ProductInfo.Version}");

        return httpClient;
    }

    // Adding an account talks to the server without a provider in between, and a provider is
    // what would otherwise turn a failed request into one of the cases the program has a
    // sentence for.
    private static ProviderException AsFailure(Uri server, HttpRequestException failure)
    {
        ProviderError error = failure.StatusCode switch
        {
            null => ProviderError.Unreachable,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderError.PermissionDenied,
            _ => ProviderError.Protocol,
        };

        return new ProviderException(error, $"The server is {server.Host}.", failure);
    }

    private static void OpenBrowser(Uri address)
    {
        try
        {
            using Process? browser = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // The address was written out first, so a browser that does not start is
            // something to work around and not a reason to give up the login.
        }
    }

    // Everything written to the console is written from a method that is not async, for the
    // reason Program gives where it does the same.
    private static void AnnounceLogin(Uri address)
    {
        Console.WriteLine("Log in to the server in a browser, and grant access there.");
        Console.WriteLine(address.AbsoluteUri);
        Console.WriteLine(
            "The default browser is opened. Use the address above for another one, for a private window, or for another machine.");
        Console.WriteLine("Waiting.");
    }

    private static void WriteAdded(string id, string filePath) =>
        Console.WriteLine($"The account '{id}' is in {filePath}.");

    private static void WriteRevoked(Uri server) =>
        Console.WriteLine($"The app password is withdrawn on {server.Host}.");

    private static void WriteShared(Uri server, string other) =>
        Console.WriteLine($"The app password stays on {server.Host}: the account '{other}' is signed in with it too.");

    private static void WriteKept(Uri server, string reason)
    {
        // The error stream, although the removal carries on: it is the one line here that
        // leaves something to be done, and it is not the answer to what was asked for.
        Console.Error.WriteLine($"The app password is still on {server.Host}. {reason}");
        Console.Error.WriteLine(
            $"It is listed there under Settings, Security, as a device named after {ProductInfo.Name}.");
    }

    private static void WriteRemoved(string id) =>
        Console.WriteLine($"The account '{id}' is gone, and so is its credential.");

    private static void WriteNoAccounts(string filePath) =>
        Console.WriteLine($"There is no account yet. {filePath} is where they go.");

    private static void WriteTable(List<string[]> rows)
    {
        int columns = rows[0].Length;
        int[] widths = new int[columns];

        foreach (string[] row in rows)
        {
            for (int column = 0; column < columns; column++)
            {
                widths[column] = Math.Max(widths[column], row[column].Length);
            }
        }

        foreach (string[] row in rows)
        {
            StringBuilder written = new();

            for (int column = 0; column < columns; column++)
            {
                // The last column is not padded: trailing spaces are what a line copied out
                // of a terminal carries with it.
                written.Append(column == columns - 1 ? row[column] : row[column].PadRight(widths[column] + 2));
            }

            Console.WriteLine(written.ToString());
        }
    }
}
