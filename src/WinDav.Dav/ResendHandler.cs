// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;

namespace WinDav.Dav;

/// <summary>
/// Sends a request once more when the connection it was to go out on had already been ended
/// by the server.
/// </summary>
/// <remarks>
/// <para>
/// A server ends an HTTP/2 connection with GOAWAY once it has served its fill of requests on
/// it, or once it has been held idle long enough. That frame names the last stream the
/// server acted on, and RFC 9113 section 6.8 says everything above it was not processed and
/// may be sent again on another connection. NO_ERROR beside it says the shutdown was an
/// orderly one and nothing on the server is wrong. The request that was on its way out fails
/// all the same, and what the person at the keyboard is shown is a folder that will not open
/// on a server that is up. So it goes out again, and only what fails the second time is
/// reported (#115).
/// </para>
/// <para>
/// It sits above the record rather than under it, so that both journeys are written down.
/// What the log is for is what went out on the wire, and a resend that hid itself would
/// leave a person counting one request where there were two.
/// </para>
/// </remarks>
internal sealed class ResendHandler : DelegatingHandler
{
    // NO_ERROR, RFC 9113 section 7: the connection was closed in order, and not because
    // something on it went wrong.
    private const long NoError = 0;

    // What is sent again, and nothing else. That the server never saw the request is a good
    // reason to believe a second one is safe, but it is the server's word about its own
    // bookkeeping, and a write that lands twice is worse than a listing that fails. These
    // four ask and change nothing: RFC 9110 section 9.2.1 for the first three, RFC 4918
    // section 9.1 for PROPFIND.
    private static readonly string[] s_repeatable = ["GET", "HEAD", "OPTIONS", "PROPFIND"];

    private readonly ILogger _log;

    internal ResendHandler(HttpMessageHandler inner, ILogger log)
        : base(inner)
    {
        _log = log;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException failure) when (Repeatable(request) && WentNowhere(failure))
        {
            if (_log.IsEnabled(LogLevel.Warning))
            {
                // At the level the failure under it was written at, because the two lines
                // belong together: a warning that a request failed, with no word of what
                // became of it, is the worse half of the story.
                _log.LogWarning(
                    "{Request} is going out again: the server had ended the connection it was to go out on.",
                    LoggingHandler.Describe(request));
            }
        }

        // On whatever connection the pool holds now, since the one that was ended is no
        // longer in it, and without a pause, because nothing is waiting to recover. Once and
        // no further: a second connection ended the same moment it was handed out is not the
        // ordinary end of one any more, and is worth hearing about.
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static bool Repeatable(HttpRequestMessage request) =>
        Array.IndexOf(s_repeatable, request.Method.Method) >= 0;

    // The narrow case and only it: the request never reached the server, so nothing over
    // there has half happened. A protocol error carrying any other code is a fault on the
    // connection and stays one.
    private static bool WentNowhere(HttpRequestException failure) =>
        failure.HttpRequestError == HttpRequestError.HttpProtocolError
        && failure.InnerException is HttpProtocolException { ErrorCode: NoError };
}
