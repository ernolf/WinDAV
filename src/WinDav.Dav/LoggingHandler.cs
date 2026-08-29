// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using WinDav.Core.Logging;

namespace WinDav.Dav;

/// <summary>
/// Writes down what went out on the wire and what came back.
/// </summary>
/// <remarks>
/// <para>
/// It sits in the pipeline rather than in <see cref="DavClient"/>, so that every request is
/// counted whoever built it, and so that what is timed is the request itself: the connection,
/// the round trip and the headers of the answer, with nothing of ours in between. The body is
/// not read here, and the record is written before it has been.
/// </para>
/// <para>
/// A request that failed is written whether anything was asked for or not, because a request
/// that failed is the thing a person went looking for. What is written for one that
/// succeeded is a line at debug, and at trace the headers of both halves as well, with
/// anything that carries a credential taken out. See decisions.md 74.
/// </para>
/// </remarks>
internal sealed class LoggingHandler : DelegatingHandler
{
    private const string Sent = "> ";
    private const string Received = "< ";

    // Milliseconds with one place. Anything finer is the clock of the machine and not the
    // time of the request.
    private const string ElapsedFormat = "0.#";

    private const char LineEnd = '\n';

    private readonly ILogger _log;

    internal LoggingHandler(HttpMessageHandler inner, ILogger log)
        : base(inner)
    {
        _log = log;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Before it goes out, so that a request which never comes back has still said what it
        // was. That is the whole difference between trace and debug on the wire.
        if (_log.IsEnabled(LogLevel.Trace))
        {
            _log.LogTrace(
                "{Request}{Headers}",
                Describe(request),
                Headers(Sent, request.Headers, request.Content?.Headers));
        }

        long started = Stopwatch.GetTimestamp();

        try
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug(
                    "{Request} {Status} in {Elapsed} ms{Headers}",
                    Describe(request),
                    (int)response.StatusCode,
                    Elapsed(started),
                    _log.IsEnabled(LogLevel.Trace)
                        ? Headers(Received, response.Headers, response.Content?.Headers)
                        : string.Empty);
            }

            return response;
        }
        catch (HttpRequestException failure)
        {
            if (_log.IsEnabled(LogLevel.Warning))
            {
                // Warning and not debug: the request did not happen, the program carries on,
                // and whoever reads the file afterwards needs this line whether they thought
                // to ask for a recording or not.
                _log.LogWarning(
                    failure,
                    "{Request} failed after {Elapsed} ms.",
                    Describe(request),
                    Elapsed(started));
            }

            throw;
        }
    }

    private static string Describe(HttpRequestMessage request) =>
        $"{request.Method.Method} {(request.RequestUri is { } address ? LogRedaction.Server(address) : "?")}";

    private static string Elapsed(long started) =>
        Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString(ElapsedFormat, CultureInfo.InvariantCulture);

    // One header per line, under the line they belong to. LogFormat indents everything after
    // the first line of a message, so the shape of it is the shape it is read in.
    private static string Headers(string mark, HttpHeaders headers, HttpHeaders? content)
    {
        StringBuilder text = new();

        Append(text, mark, headers);

        if (content is not null)
        {
            Append(text, mark, content);
        }

        return text.ToString();
    }

    private static void Append(StringBuilder text, string mark, HttpHeaders headers)
    {
        foreach (KeyValuePair<string, IEnumerable<string>> header in headers)
        {
            text.Append(LineEnd)
                .Append(mark)
                .Append(header.Key)
                .Append(": ")
                .Append(LogRedaction.Header(header.Key, header.Value));
        }
    }
}
