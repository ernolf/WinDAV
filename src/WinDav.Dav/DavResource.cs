// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Xml.Linq;

namespace WinDav.Dav;

/// <summary>
/// A resource as described by one <c>DAV:response</c>, with the properties of RFC 4918
/// section 15 read into the types the rest of the program works with.
/// </summary>
/// <remarks>
/// A value the server did not deliver, or delivered in a shape that cannot be read, is
/// reported as absent. A single odd entry must not make a whole listing unusable, and
/// every one of these properties is optional to begin with.
/// </remarks>
public sealed class DavResource
{
    // RFC 4918 section 15.7 prescribes an rfc1123-date for getlastmodified, which is
    // always stated in GMT.
    private const string LastModifiedFormat = "ddd, dd MMM yyyy HH:mm:ss 'GMT'";

    private DavResource(string href, IReadOnlyDictionary<XName, XElement> properties)
    {
        Href = href;
        Properties = properties;

        IsCollection = ReadIsCollection(properties);
        ContentLength = ReadContentLength(properties);
        LastModified = ReadLastModified(properties);
        ETag = ReadText(properties, DavNames.GetETag);
        ContentType = ReadText(properties, DavNames.GetContentType);
    }

    /// <summary>
    /// Gets the href exactly as the server wrote it. See <see cref="DavResponse.Href"/>.
    /// </summary>
    public string Href { get; }

    /// <summary>
    /// Gets the properties the server delivered, keyed by their XML name. This is where a
    /// provider finds the properties of its own vendor namespace.
    /// </summary>
    public IReadOnlyDictionary<XName, XElement> Properties { get; }

    /// <summary>
    /// Gets a value indicating whether the resource is a collection, that is a directory.
    /// </summary>
    public bool IsCollection { get; }

    /// <summary>
    /// Gets the size in bytes, or <see langword="null"/> when the server did not state one.
    /// Collections have no size, so absence is the normal case for them.
    /// </summary>
    public long? ContentLength { get; }

    /// <summary>
    /// Gets the time of the last modification in UTC, or <see langword="null"/> when the
    /// server did not state one.
    /// </summary>
    public DateTimeOffset? LastModified { get; }

    /// <summary>
    /// Gets the entity tag as the server wrote it, quotes and any weakness prefix included,
    /// because that is the form a conditional request has to send back.
    /// </summary>
    public string? ETag { get; }

    /// <summary>
    /// Gets the media type, parameters such as <c>charset</c> included, or
    /// <see langword="null"/> when the server did not state one.
    /// </summary>
    public string? ContentType { get; }

    /// <summary>
    /// Reads the properties of a single response.
    /// </summary>
    /// <param name="response">The response to read.</param>
    /// <returns>The resource the response describes.</returns>
    public static DavResource FromResponse(DavResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        // Only what the server reported under 200 carries a value. A property listed
        // under 404 is named in the body but empty, and reading it would turn "the
        // server does not have this" into "the value is zero".
        Dictionary<XName, XElement> properties = [];
        foreach (DavPropertyStatus propertyStatus in response.PropertyStatuses)
        {
            if (propertyStatus.StatusCode != 200)
            {
                continue;
            }

            foreach (KeyValuePair<XName, XElement> property in propertyStatus.Properties)
            {
                properties[property.Key] = property.Value;
            }
        }

        return new DavResource(response.Href, properties);
    }

    private static bool ReadIsCollection(IReadOnlyDictionary<XName, XElement> properties)
    {
        // resourcetype holds the type as a child element and is empty for a plain file.
        return properties.TryGetValue(DavNames.ResourceType, out XElement? resourceType)
            && resourceType.Element(DavNames.Collection) is not null;
    }

    private static long? ReadContentLength(IReadOnlyDictionary<XName, XElement> properties)
    {
        string? text = ReadText(properties, DavNames.GetContentLength);
        if (text is null)
        {
            return null;
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long length)
            ? length
            : null;
    }

    private static DateTimeOffset? ReadLastModified(IReadOnlyDictionary<XName, XElement> properties)
    {
        string? text = ReadText(properties, DavNames.GetLastModified);
        if (text is null)
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(
            text,
            LastModifiedFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset lastModified)
            ? lastModified
            : null;
    }

    private static string? ReadText(IReadOnlyDictionary<XName, XElement> properties, XName name)
    {
        if (!properties.TryGetValue(name, out XElement? element))
        {
            return null;
        }

        string text = element.Value;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
