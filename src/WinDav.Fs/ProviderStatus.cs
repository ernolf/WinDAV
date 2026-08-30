// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Fsp;
using WinDav.Abstractions;

namespace WinDav.Fs;

/// <summary>
/// Turns a provider's failure into a status code Windows can put into a sentence.
/// </summary>
/// <remarks>
/// <para>
/// Windows phrases its own error messages from the Win32 code behind an NTSTATUS. When a
/// file system answers with a status Windows has no wording for, the Explorer falls back to
/// a generic dialog reading <c>0x8000FFFF</c>, "catastrophic failure", which tells the
/// person at the keyboard nothing at all. Every refusal therefore has to name a case
/// Windows already knows.
/// </para>
/// <para>
/// The mapping is deliberately coarse: a <see cref="ProviderError"/> is what a caller has to
/// act on, and several of them collapse onto the same Windows sentence. Where the seam is
/// less precise than Windows would allow, the comment says so rather than the code
/// pretending otherwise.
/// </para>
/// </remarks>
public static class ProviderStatus
{
    /// <summary>
    /// Finds the status that stands for a failure.
    /// </summary>
    /// <param name="error">The case the provider reported.</param>
    /// <returns>An NTSTATUS value, always one that fails.</returns>
    public static int From(ProviderError error) => error switch
    {
        ProviderError.NotFound => FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND,

        ProviderError.AlreadyExists => FileSystemBase.STATUS_OBJECT_NAME_COLLISION,

        // Covers both "you may not" and "you are not who you said you were". The seam does
        // not tell the two apart, and STATUS_LOGON_FAILURE would claim it does.
        ProviderError.PermissionDenied => FileSystemBase.STATUS_ACCESS_DENIED,

        // Somebody else wrote first. Windows has no status for a lost race; this one is
        // phrased "the process cannot access the file because it is being used by another
        // process", which is the closest true thing it can say.
        ProviderError.PreconditionFailed => FileSystemBase.STATUS_SHARING_VIOLATION,

        // The seam folds a missing parent and a non-empty directory into one case. The
        // missing parent is the one that reaches here in practice, because deletion is
        // recursive, so that is the wording chosen.
        ProviderError.Conflict => FileSystemBase.STATUS_OBJECT_PATH_NOT_FOUND,

        ProviderError.InsufficientStorage => FileSystemBase.STATUS_DISK_FULL,

        // Everything from a refused connection to an expired certificate arrives as this
        // one case, so the wording has to stay as broad as the case is.
        ProviderError.Unreachable => FileSystemBase.STATUS_UNEXPECTED_NETWORK_ERROR,

        // Phrased "the network is busy", which is the one sentence Windows has for a server
        // that answered and would not do the work. It reads as something to try again, which
        // is what a 423 or a 503 is, and no other status Windows knows says that of a share.
        ProviderError.Busy => FileSystemBase.STATUS_NETWORK_BUSY,

        ProviderError.Protocol => FileSystemBase.STATUS_IO_DEVICE_ERROR,

        _ => FileSystemBase.STATUS_UNEXPECTED_IO_ERROR,
    };

    /// <summary>
    /// Finds the status that stands for a failure a provider raised.
    /// </summary>
    /// <param name="exception">What the provider threw.</param>
    /// <returns>An NTSTATUS value, always one that fails.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is null.</exception>
    public static int From(ProviderException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return From(exception.Error);
    }
}
