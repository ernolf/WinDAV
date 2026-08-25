// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Abstractions;

namespace WinDav.Dav;

/// <summary>
/// Turns the paths of the seam into URIs and back. This is the only place where escaping
/// happens, in either direction.
/// </summary>
public static class DavPath
{
    /// <summary>
    /// Makes sure a base URI names a collection, because a relative reference is resolved
    /// against the last slash and would otherwise replace the last segment.
    /// </summary>
    /// <param name="uri">The URI to read as a collection.</param>
    /// <returns>The same URI, ending in a slash.</returns>
    public static Uri AsCollection(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);
    }

    /// <summary>
    /// Brings a path into the form the seam prescribes: leading slash, no trailing one.
    /// </summary>
    /// <param name="path">The path to bring into form.</param>
    /// <returns>The normalised path, which for the root is <c>"/"</c>.</returns>
    /// <exception cref="ArgumentException">The path is empty or does not start with a slash.</exception>
    public static string Normalise(string path)
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
    /// <param name="baseUri">The collection the seam's root stands for.</param>
    /// <param name="path">The path of the resource.</param>
    /// <returns>The absolute URI of that resource.</returns>
    public static Uri ToUri(Uri baseUri, string path) =>
        new(baseUri, Relative(path));

    /// <summary>
    /// Names a collection on the server, with the trailing slash MKCOL wants.
    /// </summary>
    /// <param name="baseUri">The collection the seam's root stands for.</param>
    /// <param name="path">The path of the collection.</param>
    /// <returns>The absolute URI of that collection, ending in a slash.</returns>
    public static Uri ToCollectionUri(Uri baseUri, string path)
    {
        string relative = Relative(path);

        return relative.Length == 0 ? baseUri : new Uri(baseUri, relative + "/");
    }

    /// <summary>
    /// Reads an href from a multistatus back into a path of the seam.
    /// </summary>
    /// <param name="baseUri">The collection the seam's root stands for.</param>
    /// <param name="href">The href the server wrote.</param>
    /// <returns>The path that href stands for.</returns>
    /// <exception cref="ProviderException">
    /// <see cref="ProviderError.Protocol"/> when the href is not a URI, or names something
    /// outside the base. Either means the answer cannot be trusted to describe this account.
    /// </exception>
    /// <remarks>
    /// Both sides are compared segment by segment and unescaped. A server is free to write a
    /// segment in an escaping of its own choosing, so <c>%40</c> here and <c>@</c> there name
    /// the same collection; comparing the written forms would call that a different one.
    /// Segments also keep a name from reaching past its own end, where a base of <c>/x</c>
    /// would otherwise swallow a sibling named <c>/xyz</c>.
    /// </remarks>
    public static string FromHref(Uri baseUri, string href)
    {
        ArgumentNullException.ThrowIfNull(baseUri);

        // An href may be an absolute URI or an absolute path; both resolve against the base.
        if (!Uri.TryCreate(baseUri, href, out Uri? absolute))
        {
            throw new ProviderException(
                ProviderError.Protocol,
                $"The server named a resource as \"{href}\", which is not a URI.");
        }

        // Without the trailing slash, so that the base is as many segments long as it names.
        string[] baseSegments = Segments(baseUri.AbsolutePath.TrimEnd('/'));
        string[] hrefSegments = Segments(absolute.AbsolutePath);

        if (hrefSegments.Length < baseSegments.Length
            || !hrefSegments.AsSpan(0, baseSegments.Length).SequenceEqual(baseSegments))
        {
            throw new ProviderException(
                ProviderError.Protocol,
                $"The server named \"{href}\", which is not below the base of this provider.");
        }

        string relative = string.Join('/', hrefSegments[baseSegments.Length..]).TrimEnd('/');

        return relative.Length == 0 ? "/" : "/" + relative;
    }

    // Unescaped one segment at a time: a %2F inside a name belongs to that name, and
    // unescaping the whole string at once would turn it into a separator.
    private static string[] Segments(string absolutePath)
    {
        string[] segments = absolutePath.Split('/');

        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = Uri.UnescapeDataString(segments[i]);
        }

        return segments;
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
