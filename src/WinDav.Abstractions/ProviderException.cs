// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Abstractions;

/// <summary>
/// The one exception a provider raises when an operation could not be carried out.
/// </summary>
/// <remarks>
/// Everything the store threw, be it a status code, a socket error or a body that could
/// not be read, is translated into a <see cref="ProviderError"/> before it crosses the
/// seam. The original stays available as <see cref="Exception.InnerException"/> for logs,
/// but nothing above the provider is allowed to depend on it.
/// </remarks>
public class ProviderException : Exception
{
    /// <summary>
    /// Initialises a new instance of the <see cref="ProviderException"/> class.
    /// </summary>
    public ProviderException()
        : this(ProviderError.Unknown, null, null)
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="ProviderException"/> class.
    /// </summary>
    /// <param name="message">What went wrong.</param>
    public ProviderException(string? message)
        : this(ProviderError.Unknown, message, null)
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="ProviderException"/> class.
    /// </summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">What the provider caught.</param>
    public ProviderException(string? message, Exception? innerException)
        : this(ProviderError.Unknown, message, innerException)
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="ProviderException"/> class.
    /// </summary>
    /// <param name="error">Which of the known cases this is.</param>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">What the provider caught.</param>
    public ProviderException(ProviderError error, string? message = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    /// <summary>
    /// Gets the case this failure belongs to.
    /// </summary>
    public ProviderError Error { get; }
}
