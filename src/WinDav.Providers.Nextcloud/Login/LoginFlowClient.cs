// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using WinDav.Dav;

namespace WinDav.Providers.Nextcloud.Login;

/// <summary>
/// Gets a password of this program's own out of a server, without ever seeing the user's.
/// </summary>
/// <remarks>
/// <para>
/// Login Flow v2, documented under
/// <see href="https://docs.nextcloud.com/server/latest/developer_manual/client_apis/LoginFlow/index.html"/>.
/// The program asks the server to begin a login and is told two things: an address for the
/// user's browser, and a token to ask under. The user logs in there, with a second factor and
/// whatever else their server asks of them, and grants access. From then on the poll answers
/// with a password that belongs to this program alone and can be revoked on its own.
/// </para>
/// <para>
/// Opening the browser is not done here. That is a decision about the machine the program
/// runs on, and this assembly is the one that knows the protocol.
/// </para>
/// <para>
/// The client does not own the <see cref="HttpClient"/> it is handed. It sends no credentials
/// of its own: both requests are anonymous, and one that arrives with an authorization header
/// is the caller's doing.
/// </para>
/// </remarks>
public sealed class LoginFlowClient
{
    /// <summary>
    /// How long to leave between two polls. The documented flow asks once a second.
    /// </summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a login is waited for. The server lets a token stand for twenty minutes, so
    /// waiting longer than that is waiting for something that can no longer happen.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(20);

    // Where a login is begun. Not an OCS endpoint: it takes no OCS-APIRequest header and its
    // answer comes without an envelope around it.
    private const string StartPath = "index.php/login/v2";

    private static readonly MediaTypeWithQualityHeaderValue s_json = new("application/json");

    private readonly HttpClient _httpClient;

    private readonly Uri _server;

    /// <summary>
    /// Initialises a new instance of the <see cref="LoginFlowClient"/> class.
    /// </summary>
    /// <param name="httpClient">The client the requests go out on.</param>
    /// <param name="server">
    /// The server the user named, <c>https://server/</c> or an instance below a path.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="server"/> is not absolute.</exception>
    public LoginFlowClient(HttpClient httpClient, Uri server)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(server);

        if (!server.IsAbsoluteUri)
        {
            throw new ArgumentException("The server has to be an absolute URI.", nameof(server));
        }

        _httpClient = httpClient;
        _server = DavPath.AsCollection(server);
    }

    /// <summary>
    /// Asks the server to begin a login.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The address for the browser and the token to poll under.</returns>
    /// <exception cref="HttpRequestException">The server did not answer with 200.</exception>
    /// <exception cref="FormatException">The answer is not a login that can be followed.</exception>
    public async Task<LoginFlowStart> StartAsync(CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(_server, StartPath))
        {
            // Nothing is sent, but a POST that declares no length at all is one some servers
            // refuse before they read it.
            Content = new ByteArrayContent([]),
        };

        request.Headers.Accept.Add(s_json);

        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw Refused(request.Method, request.RequestUri, response.StatusCode);
        }

        LoginFlowStart start = await ReadAsync(response, NextcloudJson.Default.LoginFlowStart, cancellationToken)
            .ConfigureAwait(false);

        // Both addresses are opened or requested later and elsewhere, where a relative one
        // would be read against something other than this server.
        if (!start.Login.IsAbsoluteUri || !start.Poll.Endpoint.IsAbsoluteUri)
        {
            throw new FormatException($"{request.RequestUri} answered with an address that is not absolute.");
        }

        return start;
    }

    /// <summary>
    /// Asks once whether the user has granted access.
    /// </summary>
    /// <param name="poll">What <see cref="StartAsync"/> answered with.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The credentials, or <see langword="null"/> while the user is still at it. A token that
    /// has expired reads the same way, which is why polling has an end; see
    /// <see cref="WaitAsync"/>.
    /// </returns>
    /// <remarks>
    /// The credentials are handed out once. An answer that is read and then dropped is a
    /// login the user has to do again.
    /// </remarks>
    /// <exception cref="HttpRequestException">The server answered neither 200 nor 404.</exception>
    /// <exception cref="FormatException">The answer holds no credentials.</exception>
    public async Task<LoginFlowCredentials?> PollAsync(LoginFlowPoll poll, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(poll);

        using HttpRequestMessage request = new(HttpMethod.Post, poll.Endpoint)
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("token", poll.Token)]),
        };

        request.Headers.Accept.Add(s_json);

        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // Nothing has gone wrong: the server says this login has no credentials to hand out,
        // which is what it says until the user has granted access.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return response.StatusCode == HttpStatusCode.OK
            ? await ReadAsync(response, NextcloudJson.Default.LoginFlowCredentials, cancellationToken).ConfigureAwait(false)
            : throw Refused(request.Method, request.RequestUri, response.StatusCode);
    }

    /// <summary>
    /// Polls until the user has granted access, or until there is no point in asking again.
    /// </summary>
    /// <param name="poll">What <see cref="StartAsync"/> answered with.</param>
    /// <param name="interval">
    /// How long to leave between two polls, or <see langword="null"/> for
    /// <see cref="DefaultInterval"/>.
    /// </param>
    /// <param name="timeout">
    /// How long to keep asking, or <see langword="null"/> for <see cref="DefaultTimeout"/>.
    /// </param>
    /// <param name="cancellationToken">Stops the waiting, which is how a user cancels a login.</param>
    /// <returns>The credentials.</returns>
    /// <exception cref="TimeoutException">Access was not granted while there was still time.</exception>
    /// <exception cref="HttpRequestException">The server answered neither 200 nor 404.</exception>
    /// <exception cref="FormatException">The answer holds no credentials.</exception>
    public async Task<LoginFlowCredentials> WaitAsync(
        LoginFlowPoll poll,
        TimeSpan? interval = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan step = interval ?? DefaultInterval;
        TimeSpan patience = timeout ?? DefaultTimeout;

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(patience, TimeSpan.Zero);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + patience;

        while (true)
        {
            LoginFlowCredentials? credentials = await PollAsync(poll, cancellationToken).ConfigureAwait(false);
            if (credentials is not null)
            {
                return credentials;
            }

            // Asked after the poll rather than before it: the last ask is worth making, and a
            // login granted in the final second is still a login.
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The login was not granted before the poll token had expired.");
            }

            await Task.Delay(step, cancellationToken).ConfigureAwait(false);
        }
    }

    private static HttpRequestException Refused(HttpMethod method, Uri? uri, HttpStatusCode status) =>
        new($"{method} {uri} was answered with {(int)status}.", inner: null, statusCode: status);

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        Uri? uri = response.RequestMessage?.RequestUri;

        using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await JsonSerializer.DeserializeAsync(body, typeInfo, cancellationToken).ConfigureAwait(false)
                ?? throw new FormatException($"{uri} answered with nothing where a login was expected.");
        }
        catch (JsonException exception)
        {
            // A field that is missing lands here as well: what the flow needs is required,
            // and the reader refuses an object that leaves one out.
            throw new FormatException($"{uri} answered with something other than a login.", exception);
        }
    }
}
