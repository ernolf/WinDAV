// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Abstractions;

/// <summary>
/// What a store has to be able to do for a file system to be mounted on it.
/// </summary>
/// <remarks>
/// <para>
/// Paths are the ones described on <see cref="RemoteEntry.Path"/>: absolute, separated by
/// slashes, unescaped. A provider turns them into whatever its store wants.
/// </para>
/// <para>
/// Every failure arrives as <see cref="ProviderException"/> with the case in
/// <see cref="ProviderException.Error"/>. Nothing of the protocol underneath is visible
/// here, which is the point of this interface.
/// </para>
/// </remarks>
public interface IStorageProvider
{
    /// <summary>
    /// Lists what is directly inside a directory.
    /// </summary>
    /// <param name="path">The directory to list.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// Its entries, without the directory itself, and what the store said about the
    /// directory where it said anything. A store that describes the directory along with
    /// its contents hands that description on rather than dropping it, because a caller
    /// that wants it would otherwise ask for what has already been answered.
    /// </returns>
    /// <exception cref="ProviderException">
    /// <see cref="ProviderError.NotFound"/> when there is no such directory.
    /// </exception>
    Task<DirectoryListing> ListAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Describes a single entry.
    /// </summary>
    /// <param name="path">The entry to describe.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What is at that path.</returns>
    /// <exception cref="ProviderException">
    /// <see cref="ProviderError.NotFound"/> when there is nothing at that path. Absence is
    /// a failure here rather than a null result, so that a caller cannot read past it by
    /// accident.
    /// </exception>
    Task<RemoteEntry> GetAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a file for reading, starting at an offset.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <param name="offset">The first byte to read, counted from zero.</param>
    /// <param name="count">
    /// How many bytes are wanted, or <see langword="null"/> for everything from
    /// <paramref name="offset"/> on.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// A stream whose first byte is the one at <paramref name="offset"/>. The caller
    /// disposes it. A provider whose store ignored the offset skips forward itself; that
    /// the promise is kept is the provider's business, not the caller's.
    /// </returns>
    /// <exception cref="ProviderException">
    /// <see cref="ProviderError.NotFound"/> when there is no such file.
    /// </exception>
    Task<Stream> OpenReadAsync(
        string path,
        long offset = 0,
        long? count = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a file, creating it or replacing what is there.
    /// </summary>
    /// <param name="path">The file to write.</param>
    /// <param name="content">
    /// The bytes to write. The stream is read to its end and left open; it belongs to the
    /// caller.
    /// </param>
    /// <param name="ifMatch">
    /// The <see cref="RemoteEntry.ETag"/> the file must still carry for the write to
    /// happen. Without one the write replaces whatever is there.
    /// </param>
    /// <param name="times">
    /// The times the file is to carry once it is written. A store that can put them into
    /// the same request as the contents does; one that cannot sets them afterwards, as
    /// <see cref="SetTimesAsync"/> would. Empty is the caller saying nothing about them,
    /// and then the file carries whatever the store gives it.
    /// </param>
    /// <param name="mustBeNew">
    /// Whether the write is only to happen where nothing is there yet. It is how a file
    /// that Windows has just made reaches the store: the name is taken and the contents are
    /// put there in one request, and somebody who got there first is a collision rather
    /// than an overwrite nobody notices. It has no meaning together with
    /// <paramref name="ifMatch"/>, which asks for the opposite.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// The entity tag of what was written, or <see langword="null"/> when the store did not
    /// state one. A store that had to set <paramref name="times"/> in a second request has
    /// none to state: the file changed again after the answer that carried the tag.
    /// </returns>
    /// <exception cref="ProviderException">
    /// <see cref="ProviderError.PreconditionFailed"/> when <paramref name="ifMatch"/> no
    /// longer holds, which is somebody else having written first, and
    /// <see cref="ProviderError.AlreadyExists"/> when <paramref name="mustBeNew"/> was
    /// asked for and something is there.
    /// </exception>
    Task<string?> WriteAsync(
        string path,
        Stream content,
        string? ifMatch = null,
        EntryTimes times = default,
        bool mustBeNew = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the times an entry carries, without touching what is in it.
    /// </summary>
    /// <param name="path">The entry, a file or a directory.</param>
    /// <param name="times">
    /// What to set. Whichever of the two is absent is left as it is, and an empty
    /// <see cref="EntryTimes"/> asks for nothing and sends nothing.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the store has been asked.</returns>
    /// <remarks>
    /// A store that does not let its times be set answers by doing nothing, and that is not
    /// a failure: a time is worth less than the entry it belongs to, and a caller that has
    /// just written a file must not be told the write failed because a date did. What is a
    /// failure is the entry being missing or refused, and that arrives as it does anywhere
    /// else.
    /// </remarks>
    /// <exception cref="ProviderException">
    /// <see cref="ProviderError.NotFound"/> when there is no such entry.
    /// </exception>
    Task SetTimesAsync(string path, EntryTimes times, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an empty file, and only where nothing is there yet.
    /// </summary>
    /// <param name="path">The file to create.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// The entity tag of what was created, or <see langword="null"/> when the store did not
    /// state one.
    /// </returns>
    /// <remarks>
    /// The counterpart of <see cref="CreateDirectoryAsync"/>: the same question asked about a
    /// file. A mount does not make its files this way — it holds the name itself until there
    /// is something to send and then writes once, with <c>mustBeNew</c>. What is left for
    /// this is a caller that wants the name taken and nothing in it.
    /// </remarks>
    /// <exception cref="ProviderException">
    /// <see cref="ProviderError.AlreadyExists"/> when something is already there,
    /// <see cref="ProviderError.Conflict"/> when the directory above it is missing.
    /// </exception>
    Task<string?> CreateFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a directory. The directory above it has to exist already.
    /// </summary>
    /// <param name="path">The directory to create.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the directory exists.</returns>
    /// <exception cref="ProviderException">
    /// <see cref="ProviderError.AlreadyExists"/> when something is already there,
    /// <see cref="ProviderError.Conflict"/> when the directory above it is missing.
    /// </exception>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an entry, with everything inside it when it is a directory.
    /// </summary>
    /// <param name="path">The entry to delete.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the entry is gone.</returns>
    /// <exception cref="ProviderException">
    /// <see cref="ProviderError.NotFound"/> when there is nothing to delete.
    /// </exception>
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves an entry, which is also how it is renamed.
    /// </summary>
    /// <param name="sourcePath">What to move.</param>
    /// <param name="destinationPath">Where it goes.</param>
    /// <param name="overwrite">Whether an entry already at the destination may be replaced.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the entry has moved.</returns>
    /// <exception cref="ProviderException">
    /// <see cref="ProviderError.AlreadyExists"/> when the destination is taken and
    /// <paramref name="overwrite"/> is <see langword="false"/>.
    /// </exception>
    Task MoveAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies an entry, with everything inside it when it is a directory.
    /// </summary>
    /// <param name="sourcePath">What to copy.</param>
    /// <param name="destinationPath">Where the copy goes.</param>
    /// <param name="overwrite">Whether an entry already at the destination may be replaced.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the copy exists.</returns>
    /// <exception cref="ProviderException">
    /// <see cref="ProviderError.AlreadyExists"/> when the destination is taken and
    /// <paramref name="overwrite"/> is <see langword="false"/>.
    /// </exception>
    Task CopyAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks how much room the store has.
    /// </summary>
    /// <param name="path">
    /// The directory the question is about. A store that keeps one figure for the whole
    /// account answers the same for every path; one that keeps a figure per directory
    /// answers for that directory.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// What the store said, with anything it did not state left absent. A store that keeps
    /// no such figures at all answers with both absent; that is not a failure.
    /// </returns>
    /// <exception cref="ProviderException">
    /// <see cref="ProviderError.NotFound"/> when there is no such directory.
    /// </exception>
    Task<StorageSpace> GetSpaceAsync(string path, CancellationToken cancellationToken = default);
}
