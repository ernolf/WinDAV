// SPDX-FileCopyrightText: 2026 ernolf
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Dav;

/// <summary>
/// One <c>DAV:response</c> element: what a server has to say about a single resource.
/// </summary>
public sealed class DavResponse
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DavResponse"/> class.
    /// </summary>
    /// <param name="href">The href naming the resource.</param>
    /// <param name="statusCode">The status for the whole resource, if the server sent one.</param>
    /// <param name="propertyStatuses">The property groups the server returned.</param>
    public DavResponse(string href, int? statusCode, IReadOnlyList<DavPropertyStatus> propertyStatuses)
    {
        ArgumentNullException.ThrowIfNull(href);
        ArgumentNullException.ThrowIfNull(propertyStatuses);

        Href = href;
        StatusCode = statusCode;
        PropertyStatuses = propertyStatuses;
    }

    /// <summary>
    /// Gets the href exactly as the server wrote it: still percent-encoded, and relative
    /// or absolute at the server's discretion. Resolving it needs the request URI, which
    /// the parser does not have.
    /// </summary>
    public string Href { get; }

    /// <summary>
    /// Gets the status that applies to the resource as a whole, or <see langword="null"/>
    /// when the server described it through <see cref="PropertyStatuses"/> instead. A
    /// response carries one form or the other, never both.
    /// </summary>
    public int? StatusCode { get; }

    /// <summary>
    /// Gets the property groups returned for this resource.
    /// </summary>
    public IReadOnlyList<DavPropertyStatus> PropertyStatuses { get; }
}
