// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Abstractions;

namespace WinDav.Providers.WebDav;

/// <summary>
/// Turns the paths of the seam into URIs and back. This is the only place in the provider
/// where escaping happens, in either direction.
/// </summary>
internal static class WebDavPath
{
    /// <summary>
    /// Makes sure a base URI names a collection, because a relative reference is resolved
    /// against the last slash and would otherwise replace the last segment.
    /// </summary>
    internal static Uri AsCollection(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);

    /// <summary>
    /// Brings a path into the form the seam prescribes: leading slash, no trailing one.
    /// </summary>
    internal static string Normalise(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (path[0] != '/')
        {
            throw new ArgumentException("A path has to start with a slash.", nameof(path));
        }

        string trimmed = path.TrimEnd('/');

        return trimmed.Length == 0 ? "/" : trimmed;
    }

    /// <summary>
    /// Names a resource on the server.
    /// </summary>
    internal static Uri ToUri(Uri baseUri, string path) =>
        new(baseUri, Relative(path));

    /// <summary>
    /// Names a collection on the server, with the trailing slash MKCOL wants.
    /// </summary>
    internal static Uri ToCollectionUri(Uri baseUri, string path)
    {
        string relative = Relative(path);

        return relative.Length == 0 ? baseUri : new Uri(baseUri, relative + "/");
    }

    /// <summary>
    /// Reads an href from a multistatus back into a path of the seam.
    /// </summary>
    /// <exception cref="ProviderException">
    /// <see cref="ProviderError.Protocol"/> when the href is not a URI, or names something
    /// outside the base. Either means the answer cannot be trusted to describe this account.
    /// </exception>
    internal static string FromHref(Uri baseUri, string href)
    {
        // An href may be an absolute URI or an absolute path; both resolve against the base.
        if (!Uri.TryCreate(baseUri, href, out Uri? absolute))
        {
            throw new ProviderException(
                ProviderError.Protocol,
                $"The server named a resource as \"{href}\", which is not a URI.");
        }

        string basePath = baseUri.AbsolutePath;
        string hrefPath = absolute.AbsolutePath;

        if (!hrefPath.StartsWith(basePath, StringComparison.Ordinal))
        {
            throw new ProviderException(
                ProviderError.Protocol,
                $"The server named \"{href}\", which is not below the base of this provider.");
        }

        string relative = hrefPath[basePath.Length..].TrimEnd('/');

        if (relative.Length == 0)
        {
            return "/";
        }

        // Unescaped one segment at a time: a %2F inside a name belongs to that name, and
        // unescaping the whole string at once would turn it into a separator.
        string[] segments = relative.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = Uri.UnescapeDataString(segments[i]);
        }

        return "/" + string.Join('/', segments);
    }

    private static string Relative(string path)
    {
        string normalised = Normalise(path);

        if (normalised.Length == 1)
        {
            return string.Empty;
        }

        string[] segments = normalised[1..].Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];

            if (segment.Length == 0)
            {
                throw new ArgumentException("A path must not hold an empty segment.", nameof(path));
            }

            // The Uri class resolves these, and ".." resolved often enough reaches above the
            // base. A path from outside must not be able to leave the account it belongs to.
            if (segment is "." or "..")
            {
                throw new ArgumentException("A path must not hold \".\" or \"..\" segments.", nameof(path));
            }

            segments[i] = Uri.EscapeDataString(segment);
        }

        return string.Join('/', segments);
    }
}
