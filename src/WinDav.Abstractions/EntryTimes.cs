// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Abstractions;

/// <summary>
/// The times an entry is to carry, as far as anybody has said.
/// </summary>
/// <param name="Created">
/// When the entry came into being, or <see langword="null"/> to leave whatever it carries.
/// </param>
/// <param name="LastModified">
/// When its contents last changed, or <see langword="null"/> to leave whatever it carries.
/// </param>
/// <remarks>
/// Each time is asked for on its own: a caller that knows one and not the other says so by
/// leaving the other absent, and a store must then not touch it. That is why this is two
/// nullable fields rather than a pair that is either there or not.
/// </remarks>
public readonly record struct EntryTimes(DateTimeOffset? Created, DateTimeOffset? LastModified)
{
    /// <summary>
    /// Gets a value indicating whether neither time was named, so there is nothing to set
    /// and nothing to send.
    /// </summary>
    public bool IsEmpty => Created is null && LastModified is null;
}
