// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Globalization;
using WinDav.Dav;

namespace WinDav.Providers.Nextcloud;

/// <summary>
/// A Nextcloud store, which is a WebDAV store that can also take a large file in pieces.
/// </summary>
/// <remarks>
/// <para>
/// Everything a Nextcloud server does over plain RFC 4918 it inherits. What it adds here is
/// the chunked upload of version 2, documented under
/// <see href="https://docs.nextcloud.com/server/latest/developer_manual/client_apis/WebDAV/chunking.html"/>:
/// the pieces are written into an upload directory of their own and the server assembles
/// them when the last one has arrived. A connection that breaks then costs one chunk instead
/// of the whole transfer.
/// </para>
/// <para>
/// The chunked path is used only when it can be used safely; see
/// <see cref="WriteAsync(string, Stream, string?, CancellationToken)"/> for the three cases
/// that fall back to a single PUT.
/// </para>
/// </remarks>
public sealed class NextcloudProvider : DavStorageProvider
{
    /// <summary>
    /// The default size of one chunk. Ten megabytes is one round trip's worth on a
    /// connection worth chunking for, and it is what is held in memory while it is sent.
    /// </summary>
    public const long DefaultChunkSize = 10L * 1024 * 1024;

    // Both from the protocol documentation: a chunk under five megabytes is refused, and
    // the name of a chunk has to be a number from 1 to 10000.
    private const long SmallestChunkSize = 5L * 1024 * 1024;

    private const int MaximumChunks = 10000;

    // Not from the documentation, which allows up to five gigabytes: a chunk is read into
    // memory before it is sent, so this is the ceiling on what one upload costs. It still
    // leaves a terabyte of file, which is past anything a file system hands over in one go.
    private const long LargestChunkSize = 100L * 1024 * 1024;

    // The name the server answers under once every chunk is in place. Moving it is what
    // triggers the assembly.
    private const string AssembledName = ".file";

    // Nextcloud wants the target on every request of an upload, not only on the last one,
    // so it can refuse early what it would have to refuse in the end anyway.
    private const string DestinationHeader = "Destination";

    // The size of the whole file, which is what a quota can be checked against. Without it
    // an upload over quota is only refused when the chunks are assembled.
    private const string TotalLengthHeader = "OC-Total-Length";

    private readonly Uri _uploads;

    private readonly long _chunkSize;

    /// <summary>
    /// Initialises a new instance of the <see cref="NextcloudProvider"/> class.
    /// </summary>
    /// <param name="client">The client the requests go out on.</param>
    /// <param name="baseUri">
    /// The collection the seam's root stands for, usually
    /// <c>https://server/remote.php/dav/files/&lt;user&gt;/</c> but any collection below it
    /// works just as well.
    /// </param>
    /// <param name="uploadsUri">
    /// The user's upload area, <c>https://server/remote.php/dav/uploads/&lt;user&gt;/</c>.
    /// It is asked for rather than worked out from <paramref name="baseUri"/>, because
    /// <paramref name="baseUri"/> may point anywhere below the user's files and a server may
    /// live under a path of its own.
    /// </param>
    /// <param name="chunkSize">The size of one chunk. See <see cref="DefaultChunkSize"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="chunkSize"/> is under the five megabytes the server insists on, or
    /// over what this provider is willing to hold in memory.
    /// </exception>
    public NextcloudProvider(DavClient client, Uri baseUri, Uri uploadsUri, long chunkSize = DefaultChunkSize)
        : base(client, baseUri)
    {
        ArgumentNullException.ThrowIfNull(uploadsUri);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, SmallestChunkSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkSize, LargestChunkSize);

        if (!uploadsUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The upload area has to be an absolute URI.", nameof(uploadsUri));
        }

        _uploads = DavPath.AsCollection(uploadsUri);
        _chunkSize = chunkSize;
    }

    /// <summary>
    /// Builds a provider for a user's whole file area, with the two URIs a stock Nextcloud
    /// uses.
    /// </summary>
    /// <param name="client">The client the requests go out on.</param>
    /// <param name="davRoot">The DAV root, <c>https://server/remote.php/dav/</c>.</param>
    /// <param name="userId">
    /// The user's identifier, which is the one in the path and not the display name.
    /// </param>
    /// <returns>A provider rooted at that user's files.</returns>
    public static NextcloudProvider ForUser(DavClient client, Uri davRoot, string userId)
    {
        ArgumentNullException.ThrowIfNull(davRoot);
        ArgumentException.ThrowIfNullOrEmpty(userId);

        Uri root = DavPath.AsCollection(davRoot);
        string segment = Uri.EscapeDataString(userId);

        return new NextcloudProvider(client, new Uri(root, $"files/{segment}/"), new Uri(root, $"uploads/{segment}/"));
    }

    /// <summary>
    /// Writes a file, in pieces when that is both possible and worth it.
    /// </summary>
    /// <param name="path">Where the file goes.</param>
    /// <param name="content">The bytes to write.</param>
    /// <param name="ifMatch">See <see cref="DavStorageProvider.WriteAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the upload and clears up after it.</param>
    /// <returns>
    /// The entity tag when the server stated one. A chunked upload has none: the answer that
    /// carries it belongs to the assembling MOVE, and the server is not obliged to send one.
    /// </returns>
    /// <remarks>
    /// Three cases go out as a single PUT instead. A stream that cannot be measured has no
    /// total length to declare, and the server needs one. A file no larger than one chunk
    /// would be a PUT with three extra requests around it. And an <paramref name="ifMatch"/>
    /// has nowhere to go in the chunked exchange, so honouring it would mean dropping it,
    /// which turns a guarded write into a lost update.
    /// </remarks>
    public override async Task<string?> WriteAsync(
        string path,
        Stream content,
        string? ifMatch = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        long? length = content.CanSeek ? content.Length - content.Position : null;

        if (ifMatch is not null || length is null || length.Value <= _chunkSize)
        {
            return await base.WriteAsync(path, content, ifMatch, cancellationToken).ConfigureAwait(false);
        }

        await UploadInChunksAsync(path, content, length.Value, cancellationToken).ConfigureAwait(false);

        return null;
    }

    private async Task UploadInChunksAsync(string path, Stream content, long length, CancellationToken cancellationToken)
    {
        Uri target = DavPath.ToUri(BaseUri, path);
        Uri folder = new(_uploads, $"windav-{Guid.NewGuid()}/");

        long chunkSize = ChunkSize(length);
        string total = length.ToString(CultureInfo.InvariantCulture);

        KeyValuePair<string, string>[] headers =
        [
            new(DestinationHeader, target.AbsoluteUri),
            new(TotalLengthHeader, total),
        ];

        try
        {
            await Client.MkColAsync(folder, headers, cancellationToken).ConfigureAwait(false);
            await SendChunksAsync(content, folder, length, chunkSize, headers, cancellationToken).ConfigureAwait(false);

            // MOVE carries the destination as a parameter of its own, so only the length
            // goes in here; passing it twice would send the header twice.
            await Client
                .MoveAsync(
                    new Uri(folder, AssembledName),
                    target,
                    overwrite: true,
                    [new(TotalLengthHeader, total)],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            await DiscardAsync(folder).ConfigureAwait(false);

            throw Failed($"Writing {DavPath.Normalise(path)}", exception);
        }
        catch
        {
            // Cancellation and a stream that came up short land here. Whatever it was, the
            // half-written upload directory is ours to take away.
            await DiscardAsync(folder).ConfigureAwait(false);

            throw;
        }
    }

    private async Task SendChunksAsync(
        Stream content,
        Uri folder,
        long length,
        long chunkSize,
        KeyValuePair<string, string>[] headers,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)chunkSize);

        try
        {
            long left = length;
            for (int number = 1; left > 0; number++)
            {
                int wanted = (int)Math.Min(chunkSize, left);
                await ReadFullyAsync(content, buffer, wanted, cancellationToken).ConfigureAwait(false);

                // The chunks are assembled in the order of their names, and the names are
                // read as text, so they are padded to the width of the largest one.
                Uri chunk = new(folder, number.ToString("D5", CultureInfo.InvariantCulture));

                using MemoryStream piece = new(buffer, 0, wanted, writable: false);
                await Client
                    .PutAsync(chunk, piece, contentType: null, ifMatch: null, headers, cancellationToken)
                    .ConfigureAwait(false);

                left -= wanted;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ReadFullyAsync(Stream content, byte[] buffer, int wanted, CancellationToken cancellationToken)
    {
        int filled = 0;
        while (filled < wanted)
        {
            int read = await content.ReadAsync(buffer.AsMemory(filled, wanted - filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "The stream ended before it had delivered the number of bytes its length promised.");
            }

            filled += read;
        }
    }

    private long ChunkSize(long length)
    {
        // Only ten thousand chunks are allowed, so past a certain size the pieces have to
        // grow rather than multiply.
        long needed = (length + MaximumChunks - 1) / MaximumChunks;
        long size = Math.Max(_chunkSize, needed);

        return size <= LargestChunkSize
            ? size
            : throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                $"A file this size would need chunks over {LargestChunkSize} bytes, which is more than one upload may hold in memory.");
    }

    private async Task DiscardAsync(Uri folder)
    {
        try
        {
            // Not the caller's token: the tidy-up matters most when the upload was
            // cancelled, and a cancelled token would skip it.
            await Client.DeleteAsync(folder, CancellationToken.None).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // The server expires an upload directory after a day of silence, so a failed
            // tidy-up costs space for a while. Letting it replace the failure that caused
            // it would cost the reason the upload went wrong.
        }
    }
}
