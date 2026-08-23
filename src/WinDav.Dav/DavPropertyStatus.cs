// SPDX-FileCopyrightText: 2026 ernolf
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Xml.Linq;

namespace WinDav.Dav;

/// <summary>
/// A group of properties that share one status, as returned in a <c>DAV:propstat</c>
/// element. A server answers a single request with several of these: the properties it
/// could deliver under 200, the ones it does not have under 404.
/// </summary>
public sealed class DavPropertyStatus
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DavPropertyStatus"/> class.
    /// </summary>
    /// <param name="statusCode">The HTTP status code that applies to the properties.</param>
    /// <param name="properties">The properties, keyed by their XML name.</param>
    public DavPropertyStatus(int statusCode, IReadOnlyDictionary<XName, XElement> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        StatusCode = statusCode;
        Properties = properties;
    }

    /// <summary>
    /// Gets the HTTP status code that applies to every property in this group.
    /// </summary>
    public int StatusCode { get; }

    /// <summary>
    /// Gets the properties, keyed by their XML name and kept as the elements the server
    /// sent. Anything beyond RFC 4918 stays intact this way, which is what lets a
    /// provider read its own properties without the protocol layer knowing them.
    /// </summary>
    public IReadOnlyDictionary<XName, XElement> Properties { get; }
}
