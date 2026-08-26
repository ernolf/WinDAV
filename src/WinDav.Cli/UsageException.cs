// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Cli;

/// <summary>
/// The command line said something that cannot be carried out as written.
/// </summary>
/// <remarks>
/// Kept apart from every other failure because it is answered differently: nothing has been
/// attempted yet, the message says what is wrong with what was typed, and the exit code says
/// that the fault was in the asking.
/// </remarks>
internal sealed class UsageException : Exception
{
    /// <summary>
    /// Initialises a new instance of the <see cref="UsageException"/> class.
    /// </summary>
    public UsageException()
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="UsageException"/> class.
    /// </summary>
    /// <param name="message">What is wrong with what was typed.</param>
    public UsageException(string? message)
        : base(message)
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="UsageException"/> class.
    /// </summary>
    /// <param name="message">What is wrong with what was typed.</param>
    /// <param name="innerException">What was caught while making sense of it.</param>
    public UsageException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
