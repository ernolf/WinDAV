// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Abstractions;

/// <summary>
/// A file on its way to a store in pieces, begun while the rest of it is still being
/// written.
/// </summary>
/// <remarks>
/// <para>
/// A store that takes a file in pieces can take the front of it before anybody knows how
/// long the file will be. The pieces go somewhere the file is not, and nothing at the path
/// changes until <see cref="FinishAsync"/> puts the whole file there, under the same
/// conditions and with the same times a <see cref="IStorageProvider.WriteAsync"/> carries.
/// See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#86-a-write-is-finished-when-the-server-has-it">decision 86</see>.
/// </para>
/// <para>
/// One call at a time: the next one waits until the last one has returned. Disposing an
/// upload that was not finished abandons it, and what it had sent is taken away. Disposing
/// one that was finished, whether the finish worked or not, only lets go of it.
/// </para>
/// </remarks>
public interface IUpload : IAsyncDisposable
{
    /// <summary>
    /// Asks how long every piece has to be.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// The length of a piece in bytes, or zero when the upload takes no pieces ahead and the
    /// whole file goes with <see cref="FinishAsync"/>.
    /// </returns>
    /// <remarks>
    /// A store may have to ask its server first, which is why this is not known before the
    /// upload is asked. The answer holds for the whole upload.
    /// </remarks>
    /// <exception cref="ProviderException">The store could not find out.</exception>
    Task<long> GetPieceSizeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the next piece of the file.
    /// </summary>
    /// <param name="piece">
    /// The bytes that follow the pieces already sent, read from where the stream stands to
    /// its end, which is exactly one piece of the length <see cref="GetPieceSizeAsync"/>
    /// gives. The stream is the upload's from here on: it is still read after this returns,
    /// while the piece travels, and the upload disposes of it whatever becomes of the piece.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> when the piece was taken; <see langword="false"/> when the
    /// upload takes no more pieces ahead, or none at all, and what is left goes with
    /// <see cref="FinishAsync"/>.
    /// </returns>
    /// <remarks>
    /// This returns once the piece is on its way, which may mean waiting for an earlier one
    /// to arrive. A piece the store refused is reported by the call after it, or by the
    /// finish. A piece that failed leaves a gap: the upload takes no more pieces and cannot be
    /// finished, only disposed.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="piece"/> is null, cannot say how long it is, or does not hold exactly
    /// one piece.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The upload has been finished, or a piece of it failed.
    /// </exception>
    /// <exception cref="ProviderException">
    /// This piece or an earlier one was refused, or the store could not find out how long a
    /// piece is.
    /// </exception>
    Task<bool> SendAsync(Stream piece, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends what is left of the file and puts the whole of it at its path.
    /// </summary>
    /// <param name="rest">
    /// The bytes that follow the pieces, read from where the stream stands to its end. It has
    /// to be able to say how long it is, and it is left open; it belongs to the caller.
    /// </param>
    /// <param name="ifMatch">See <see cref="IStorageProvider.WriteAsync"/>.</param>
    /// <param name="times">See <see cref="IStorageProvider.WriteAsync"/>.</param>
    /// <param name="mustBeNew">See <see cref="IStorageProvider.WriteAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What <see cref="IStorageProvider.WriteAsync"/> returns.</returns>
    /// <remarks>
    /// An upload that took no pieces has nothing to add the rest to, and the rest is written
    /// as <see cref="IStorageProvider.WriteAsync"/> writes a file.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="rest"/> is null or cannot say how long it is.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The upload has been finished already, or a piece of it failed before.
    /// </exception>
    /// <exception cref="ProviderException">
    /// What <see cref="IStorageProvider.WriteAsync"/> throws, and a refused piece that no
    /// earlier call reported.
    /// </exception>
    Task<string?> FinishAsync(
        Stream rest,
        string? ifMatch = null,
        EntryTimes times = default,
        bool mustBeNew = false,
        CancellationToken cancellationToken = default);
}
