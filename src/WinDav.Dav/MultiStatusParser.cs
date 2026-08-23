// SPDX-FileCopyrightText: 2026 ernolf
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace WinDav.Dav;

/// <summary>
/// Reads the <c>DAV:multistatus</c> body a server returns with 207, as defined in
/// RFC 4918 section 13.
/// </summary>
public static class MultiStatusParser
{
    /// <summary>
    /// Reads a multistatus body from a stream.
    /// </summary>
    /// <param name="stream">The response body.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One entry per <c>DAV:response</c> element, in document order.</returns>
    /// <exception cref="FormatException">The body is not a well formed multistatus.</exception>
    public static async Task<IReadOnlyList<DavResponse>> ParseAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // The body comes off the network. A DTD or an external entity would let the
        // server pull local files into the parse, so both are refused outright.
        XmlReaderSettings settings = new()
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        using XmlReader reader = XmlReader.Create(stream, settings);
        XDocument document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);

        return Parse(document);
    }

    /// <summary>
    /// Reads a multistatus body that is already parsed as XML.
    /// </summary>
    /// <param name="document">The response body.</param>
    /// <returns>One entry per <c>DAV:response</c> element, in document order.</returns>
    /// <exception cref="FormatException">The body is not a well formed multistatus.</exception>
    public static IReadOnlyList<DavResponse> Parse(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        XElement root = document.Root ?? throw new FormatException("The multistatus body is empty.");
        if (root.Name != DavNames.MultiStatus)
        {
            throw new FormatException($"Expected a {DavNames.MultiStatus} root element but found {root.Name}.");
        }

        List<DavResponse> responses = [];
        foreach (XElement response in root.Elements(DavNames.Response))
        {
            responses.Add(ParseResponse(response));
        }

        return responses;
    }

    private static DavResponse ParseResponse(XElement response)
    {
        string href = response.Element(DavNames.Href)?.Value
            ?? throw new FormatException("A response element carries no href.");

        List<DavPropertyStatus> propertyStatuses = [];
        foreach (XElement propStat in response.Elements(DavNames.PropStat))
        {
            propertyStatuses.Add(ParsePropertyStatus(propStat));
        }

        return new DavResponse(href, ReadStatus(response.Element(DavNames.Status)), propertyStatuses);
    }

    private static DavPropertyStatus ParsePropertyStatus(XElement propStat)
    {
        int statusCode = ReadStatus(propStat.Element(DavNames.Status))
            ?? throw new FormatException("A propstat element carries no status.");

        Dictionary<XName, XElement> properties = [];
        foreach (XElement property in propStat.Element(DavNames.Prop)?.Elements() ?? [])
        {
            // A sane server does not send a property twice. If one does, the last
            // occurrence wins instead of the whole listing failing.
            properties[property.Name] = property;
        }

        return new DavPropertyStatus(statusCode, properties);
    }

    private static int? ReadStatus(XElement? status)
    {
        if (status is null)
        {
            return null;
        }

        // A status line reads "HTTP/1.1 200 OK"; only the code is of interest, and the
        // reason phrase is free text that must not be relied on.
        string line = status.Value;
        int code = line.IndexOf(' ') + 1;
        if (code <= 0 || code + 3 > line.Length
            || !int.TryParse(line.AsSpan(code, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
        {
            throw new FormatException($"Cannot read a status code from '{line}'.");
        }

        return parsed;
    }
}
