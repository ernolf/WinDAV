// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Xml.Linq;

namespace WinDav.Dav;

/// <summary>
/// The XML names RFC 4918 defines.
/// </summary>
public static class DavNames
{
    /// <summary>
    /// The namespace every element of the protocol lives in. Note that it is the literal
    /// string <c>DAV:</c>, not a URL: RFC 4918 section 21 defines it that way.
    /// </summary>
    public static readonly XNamespace Namespace = "DAV:";

    /// <summary>The <c>DAV:multistatus</c> element, root of a 207 response body.</summary>
    public static readonly XName MultiStatus = Namespace + "multistatus";

    /// <summary>The <c>DAV:response</c> element, one per resource.</summary>
    public static readonly XName Response = Namespace + "response";

    /// <summary>The <c>DAV:href</c> element naming the resource.</summary>
    public static readonly XName Href = Namespace + "href";

    /// <summary>The <c>DAV:propstat</c> element pairing properties with a status.</summary>
    public static readonly XName PropStat = Namespace + "propstat";

    /// <summary>The <c>DAV:prop</c> element holding the properties themselves.</summary>
    public static readonly XName Prop = Namespace + "prop";

    /// <summary>The <c>DAV:status</c> element carrying an HTTP status line.</summary>
    public static readonly XName Status = Namespace + "status";

    /// <summary>The <c>DAV:propfind</c> element, root of a PROPFIND request body.</summary>
    public static readonly XName PropFind = Namespace + "propfind";

    /// <summary>The <c>DAV:allprop</c> element, asking for every property the server has.</summary>
    public static readonly XName AllProp = Namespace + "allprop";

    /// <summary>The <c>DAV:resourcetype</c> property, which tells a collection from a file.</summary>
    public static readonly XName ResourceType = Namespace + "resourcetype";

    /// <summary>The <c>DAV:collection</c> element, found inside <c>DAV:resourcetype</c>.</summary>
    public static readonly XName Collection = Namespace + "collection";

    /// <summary>The <c>DAV:getcontentlength</c> property, the size in bytes.</summary>
    public static readonly XName GetContentLength = Namespace + "getcontentlength";

    /// <summary>The <c>DAV:getlastmodified</c> property, an rfc1123-date.</summary>
    public static readonly XName GetLastModified = Namespace + "getlastmodified";

    /// <summary>The <c>DAV:getetag</c> property, the entity tag.</summary>
    public static readonly XName GetETag = Namespace + "getetag";

    /// <summary>The <c>DAV:getcontenttype</c> property, the media type.</summary>
    public static readonly XName GetContentType = Namespace + "getcontenttype";
}
