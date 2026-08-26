// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Abstractions;

/// <summary>
/// How much room a store has, as far as it is willing to say.
/// </summary>
/// <remarks>
/// The two figures are separate questions, and a store may answer either, both or neither.
/// An account without a limit is the case that makes the difference plain: what it holds is
/// a real number, and what is left in it is no number at all. That is absence, not zero, and
/// nothing may be worked out from it.
/// </remarks>
public sealed class StorageSpace
{
    /// <summary>
    /// Gets the answer of a store that stated nothing.
    /// </summary>
    public static StorageSpace Unknown { get; } = new();

    /// <summary>
    /// Gets how many bytes are in use, or <see langword="null"/> when the store did not say.
    /// </summary>
    public long? Used { get; init; }

    /// <summary>
    /// Gets how many bytes may still be written, or <see langword="null"/> when the store did
    /// not say. See the remarks on this class for when there is no such figure to state.
    /// </summary>
    public long? Available { get; init; }
}
