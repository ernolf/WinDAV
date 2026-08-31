// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace WinDav.Core.Security;

/// <summary>
/// Keeps credentials as files, each encrypted for the user that wrote it.
/// </summary>
/// <remarks>
/// <para>
/// One file per credential, below a directory this store is given. What protects them is
/// DPAPI: the key belongs to the signed-in user and is held by Windows, so another account
/// on the same machine reads a file it cannot open, and a copy taken to another machine is
/// of no use to anyone. It is the only Windows-only type in this project, which is why it
/// carries the attribute instead of the project carrying a Windows target framework.
/// </para>
/// <para>
/// It goes below <see cref="ProductInfo.LocalDataDirectory"/> and not next to the
/// configuration, which roams. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#68-two-secret-stores-behind-one-seam-dpapi-first">decision 68</see> for the whole of it, and for the second store that
/// exists for the roaming case.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    /// <summary>
    /// The name of the directory inside <see cref="ProductInfo.LocalDataDirectory"/>.
    /// </summary>
    public const string DirectoryName = "secrets";

    private const string FileExtension = ".bin";

    // Same as the configuration: written beside the file and renamed over it, so a write
    // that is interrupted leaves the credential that was there whole.
    private const string TemporarySuffix = ".new";

    /// <summary>
    /// Initialises a new instance of the <see cref="DpapiSecretStore"/> class.
    /// </summary>
    /// <param name="directoryPath">The directory the credentials are kept in.</param>
    public DpapiSecretStore(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        DirectoryPath = directoryPath;
    }

    /// <summary>
    /// Gets the directory the credentials are kept in.
    /// </summary>
    public string DirectoryPath { get; }

    /// <summary>
    /// Builds a store over the directory in the product's own local data directory.
    /// </summary>
    /// <returns>A store over <see cref="DirectoryName"/> below <see cref="ProductInfo.LocalDataDirectory"/>.</returns>
    public static DpapiSecretStore Default() =>
        new(Path.Combine(ProductInfo.LocalDataDirectory, DirectoryName));

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="reference"/> cannot be a file name.</exception>
    /// <exception cref="InvalidOperationException">
    /// There is a file and this user cannot open it, which is what a credential written by
    /// someone else or carried over from another machine looks like.
    /// </exception>
    public async Task<string?> GetAsync(string reference, CancellationToken cancellationToken = default)
    {
        string path = PathFor(reference);

        if (!File.Exists(path))
        {
            return null;
        }

        byte[] encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException locked)
        {
            throw new InvalidOperationException(
                $"The credential '{reference}' is in {path} and cannot be opened by this user on this machine.",
                locked);
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">
    /// <paramref name="reference"/> cannot be a file name, or <paramref name="secret"/> is
    /// empty. A credential that is not there is removed rather than stored.
    /// </exception>
    public async Task SetAsync(string reference, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        string path = PathFor(reference);
        string temporary = path + TemporarySuffix;

        Directory.CreateDirectory(DirectoryPath);

        // No entropy of our own beside the key Windows holds. It would have to be kept
        // somewhere this program can read it, and so could anything else running as this
        // user, which is the one thing DPAPI does not protect against anyway.
        byte[] encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secret),
            null,
            DataProtectionScope.CurrentUser);

        try
        {
            await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken).ConfigureAwait(false);

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            Discard(temporary);

            throw;
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="reference"/> cannot be a file name.</exception>
    public Task RemoveAsync(string reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string path = PathFor(reference);

        // A missing file is not a failure, and neither is a directory that was never made:
        // File.Delete passes over the first in silence and throws over the second.
        if (Directory.Exists(DirectoryPath))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private static void Discard(string path)
    {
        // Whatever went wrong, the half-written file is ours to take away, and failing at
        // that must not replace the failure that led here.
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The next write renames over it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    private string PathFor(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        foreach (char written in reference)
        {
            // Refused rather than replaced: a reference that is quietly rewritten does not
            // find its credential again the next time it is asked for.
            if (!char.IsAsciiLetterOrDigit(written) && written is not ('.' or '-' or '_' or '@'))
            {
                throw new ArgumentException(
                    $"'{reference}' cannot be a file name: '{written}' is not a letter, a digit, or one of . - _ @",
                    nameof(reference));
            }
        }

        return Path.Combine(DirectoryPath, reference + FileExtension);
    }
}
