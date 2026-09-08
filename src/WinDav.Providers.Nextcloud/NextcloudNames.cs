// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Xml.Linq;
using WinDav.Dav;

namespace WinDav.Providers.Nextcloud;

/// <summary>
/// The XML names of the properties this provider asks a Nextcloud server for, beyond the
/// ones RFC 4918 defines.
/// </summary>
/// <remarks>
/// The namespaces are the ones the server's own documentation lists under
/// <see href="https://docs.nextcloud.com/server/latest/developer_manual/client_apis/WebDAV/basic.html"/>.
/// Only what is asked for is named here. The server offers a great deal more, and what a
/// PROPFIND asked for arrives whole, so anything named in the request stays readable among
/// the properties of the described resource.
/// </remarks>
public static class NextcloudNames
{
    /// <summary>
    /// The namespace the properties inherited from ownCloud live in. It is a URL only in
    /// form: nothing is served under it.
    /// </summary>
    public static readonly XNamespace OwncloudNamespace = "http://owncloud.org/ns";

    /// <summary>The namespace the properties Nextcloud added live in.</summary>
    public static readonly XNamespace NextcloudNamespace = "http://nextcloud.org/ns";

    /// <summary>
    /// The <c>oc:id</c> property, the file's identifier with the identifier of the instance
    /// behind it. Unlike <c>oc:fileid</c> it stays unique when two servers meet, which is
    /// what a federated share is.
    /// </summary>
    public static readonly XName Id = OwncloudNamespace + "id";

    /// <summary>
    /// The <c>oc:permissions</c> property, one letter per permission the user has over the
    /// entry.
    /// </summary>
    public static readonly XName Permissions = OwncloudNamespace + "permissions";

    /// <summary>
    /// The <c>DAV:lastmodified</c> property, a unix timestamp, which the server accepts in
    /// a PROPPATCH and sets the modification time from.
    /// </summary>
    /// <remarks>
    /// It is in the <c>DAV:</c> namespace and it is not in RFC 4918, which is why it is
    /// named here and not in <see cref="DavNames"/>. The protocol's own
    /// <c>DAV:getlastmodified</c> is protected and the server refuses it; this is the name
    /// it takes instead, inherited from ownCloud along with the rest.
    /// </remarks>
    public static readonly XName LastModified = DavNames.Namespace + "lastmodified";
}
